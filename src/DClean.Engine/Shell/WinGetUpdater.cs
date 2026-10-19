using DClean.Contracts;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace DClean.Engine.Shell;

[SupportedOSPlatform("windows")]
public sealed class WinGetUpdater(ILogger<WinGetUpdater> log)
{
    // ── Availability check ────────────────────────────────────────────────────

    public bool IsWinGetAvailable()
    {
        try
        {
            using var p = Run("winget", "--version");
            p.WaitForExit(5_000);
            return p.ExitCode == 0;
        }
        catch (Exception ex) { log.LogDebug(ex, "winget not available"); return false; }
    }

    // ── List outdated packages ─────────────────────────────────────────────────

    public async Task<IReadOnlyList<WinGetPackage>> GetOutdatedAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var packages = new List<WinGetPackage>();

        await Task.Run(() =>
        {
            progress?.Report("מריץ: winget upgrade...");
            string output = RunCapture("winget",
                "upgrade --include-unknown --accept-source-agreements");

            /* winget output format (locale-independent table):
               Name              Id               Version    Available  Source
               ─────────────────────────────────────────────────────────────────
               Example App       Example.App      1.0.0      2.0.0      winget
            */

            bool headerPassed = false;
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.TrimEnd('\r');

                if (!headerPassed)
                {
                    // Skip until we see a line that is mostly dashes
                    if (Regex.IsMatch(line, @"^[-─═\s]{5,}$")) { headerPassed = true; }
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line)) continue;
                // Skip summary lines like "X upgrades available"
                if (Regex.IsMatch(line, @"^\d+ upgrade")) continue;

                var pkg = ParseUpgradeLine(line);
                if (pkg != null) packages.Add(pkg);
            }
        }, ct).ConfigureAwait(false);

        log.LogInformation("WinGet found {N} outdated packages", packages.Count);
        return packages;
    }

    public async Task<IReadOnlyList<WinGetPackage>> GetInstalledAsync(
        CancellationToken ct = default)
    {
        var packages = new List<WinGetPackage>();

        await Task.Run(() =>
        {
            string output = RunCapture("winget",
                "list --accept-source-agreements");

            bool headerPassed = false;
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (!headerPassed)
                {
                    if (Regex.IsMatch(line, @"^[-─═\s]{5,}$")) { headerPassed = true; }
                    continue;
                }
                if (string.IsNullOrWhiteSpace(line)) continue;
                var pkg = ParseListLine(line);
                if (pkg != null) packages.Add(pkg);
            }
        }, ct).ConfigureAwait(false);

        return packages;
    }

    // ── Upgrade ───────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<string> UpgradeAsync(
        string packageId,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct = default)
    {
        using var p = Run("winget",
            $"upgrade --id \"{packageId}\" --silent --accept-source-agreements " +
            "--accept-package-agreements");

        while (!p.StandardOutput.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            string? line = await p.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            if (line != null) yield return line;
        }

        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        yield return p.ExitCode == 0
            ? $"✅ {packageId} עודכן בהצלחה"
            : $"❌ {packageId} נכשל (exit {p.ExitCode})";
    }

    public async IAsyncEnumerable<string> UpgradeAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct = default)
    {
        using var p = Run("winget",
            "upgrade --all --silent --accept-source-agreements --accept-package-agreements");

        while (!p.StandardOutput.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            string? line = await p.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            if (line != null) yield return line;
        }

        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        yield return p.ExitCode == 0 ? "✅ עדכון כל החבילות הושלם" : $"❌ נכשל (exit {p.ExitCode})";
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static WinGetPackage? ParseUpgradeLine(string line)
    {
        // Split on 2+ spaces — winget uses padding
        var cols = Regex.Split(line.Trim(), @"\s{2,}");
        if (cols.Length < 4) return null;

        return new WinGetPackage(
            Name:             cols[0].Trim(),
            Id:               cols.Length > 1 ? cols[1].Trim() : string.Empty,
            InstalledVersion: cols.Length > 2 ? cols[2].Trim() : "?",
            AvailableVersion: cols.Length > 3 ? cols[3].Trim() : string.Empty,
            Source:           cols.Length > 4 ? cols[4].Trim() : "winget",
            HasUpdate:        true);
    }

    private static WinGetPackage? ParseListLine(string line)
    {
        var cols = Regex.Split(line.Trim(), @"\s{2,}");
        if (cols.Length < 2) return null;

        return new WinGetPackage(
            Name:             cols[0].Trim(),
            Id:               cols.Length > 1 ? cols[1].Trim() : string.Empty,
            InstalledVersion: cols.Length > 2 ? cols[2].Trim() : "?",
            AvailableVersion: string.Empty,
            Source:           cols.Length > 3 ? cols[3].Trim() : string.Empty,
            HasUpdate:        false);
    }

    private static Process Run(string exe, string args) =>
        Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        })!;

    private string RunCapture(string exe, string args)
    {
        try
        {
            using var p = Run(exe, args);
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(60_000);
            return output;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "RunCapture failed for {Exe}", exe);
            return string.Empty;
        }
    }
}
