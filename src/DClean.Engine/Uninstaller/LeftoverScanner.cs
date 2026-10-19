using DClean.Contracts;
using DClean.Engine.Safety;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Buffers;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace DClean.Engine.Uninstaller;

[SupportedOSPlatform("windows")]
public sealed class LeftoverScanner(PathValidator validator, ILogger<LeftoverScanner> log)
{
    private static readonly string[] FileSystemRoots =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "..", "LocalLow"),
    ];

    private static readonly string[] RegistryRoots =
    [
        @"SOFTWARE",
        @"SOFTWARE\WOW6432Node",
    ];

    public async Task<LeftoverScanResult> ScanAsync(
        InstalledApp app, CancellationToken ct = default)
    {
        var candidates = new List<LeftoverCandidate>();

        await Task.Run(() =>
        {
            ScanFileSystem(app, candidates, ct);
            ScanRegistry(app, candidates, ct);
            ScanServices(app, candidates, ct);
        }, ct).ConfigureAwait(false);

        long totalBytes = candidates
            .Where(c => c.Type is LeftoverType.File or LeftoverType.Directory)
            .Sum(c => EstimateSize(c.Path));

        return new LeftoverScanResult(
            candidates.Where(c => c.Score >= 0.5f)
                      .OrderByDescending(c => c.Score)
                      .ToList(),
            totalBytes);
    }

    // ── File System ──────────────────────────────────────────────────────────

    private void ScanFileSystem(InstalledApp app, List<LeftoverCandidate> results,
        CancellationToken ct)
    {
        string[] searchNames = BuildSearchNames(app);

        foreach (string root in FileSystemRoots)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;

            try
            {
                foreach (string dir in Directory.GetDirectories(root))
                {
                    ct.ThrowIfCancellationRequested();
                    if (validator.IsSystemProtectedPath(dir, out _)) continue;
                    string dirName = Path.GetFileName(dir);
                    float score = ComputeNameScore(dirName, searchNames, app.Guid);
                    if (score > 0f)
                        results.Add(new LeftoverCandidate(dir, LeftoverType.Directory,
                            score, BuildReason(dirName, app, score),
                            Source: "File system scan",
                            RelatedApplication: app.DisplayName,
                            Location: root,
                            ProblematicItem: dir));
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (Exception ex) { log.LogDebug(ex, "FS scan error in {Root}", root); }
        }
    }

    // ── Registry ─────────────────────────────────────────────────────────────

    private void ScanRegistry(InstalledApp app, List<LeftoverCandidate> results,
        CancellationToken ct)
    {
        string[] searchNames = BuildSearchNames(app);

        foreach (var (hive, hiveName) in new[]
        {
            (RegistryHive.LocalMachine, "HKLM"),
            (RegistryHive.CurrentUser,  "HKCU"),
        })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                ct.ThrowIfCancellationRequested();
                foreach (string regRoot in RegistryRoots)
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using var root    = baseKey.OpenSubKey(regRoot);
                        if (root == null) continue;

                        foreach (string subKeyName in root.GetSubKeyNames())
                        {
                            ct.ThrowIfCancellationRequested();
                            float score = ComputeNameScore(subKeyName, searchNames, app.Guid);
                            if (score > 0f)
                            {
                                string fullPath = $@"{hiveName}\{regRoot}\{subKeyName}";
                                results.Add(new LeftoverCandidate(fullPath,
                                    LeftoverType.RegistryKey, score,
                                    BuildReason(subKeyName, app, score),
                                    Source: $"Registry scan ({hiveName})",
                                    RelatedApplication: app.DisplayName,
                                    Location: regRoot,
                                    ProblematicItem: fullPath));
                            }
                        }
                    }
                    catch (Exception ex) { log.LogDebug(ex, "Error scanning registry {Root}", regRoot); }
                }
            }
        }

        // Also check Run keys
        ScanRunKeys(app, results, searchNames);
        // And CLSID if we have a GUID
        if (app.Guid != null)
            ScanClsid(app.Guid, results);
    }

    private void ScanRunKeys(InstalledApp app, List<LeftoverCandidate> results,
        string[] searchNames)
    {
        string[] runPaths =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
        ];

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (string runPath in runPaths)
            {
                try
                {
                    using var key = Registry.OpenKey(hive, runPath);
                    if (key == null) continue;
                    foreach (string valueName in key.GetValueNames())
                    {
                        float score = ComputeNameScore(valueName, searchNames, app.Guid);
                        string? cmd = key.GetValue(valueName) as string ?? string.Empty;
                        if (score == 0f && !string.IsNullOrEmpty(app.InstallLocation))
                            score = cmd.Contains(app.InstallLocation,
                                StringComparison.OrdinalIgnoreCase) ? 0.8f : 0f;
                        if (score > 0f)
                            results.Add(new LeftoverCandidate(
                                $@"{hive}\{runPath}\{valueName}",
                                LeftoverType.RegistryKey, score,
                                $"Run key value matches app",
                                Source: "Startup registry value",
                                RelatedApplication: app.DisplayName,
                                Location: runPath,
                                ProblematicItem: cmd));
                    }
                }
                catch (Exception ex) { log.LogDebug(ex, "Error scanning run key {Path}", runPath); }
            }
        }
    }

    private void ScanClsid(string guid, List<LeftoverCandidate> results)
    {
        string normalizedGuid = guid.Trim('{', '}');
        string clsidPath = $@"CLSID\{{{normalizedGuid}}}";
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(clsidPath);
            if (key != null)
                results.Add(new LeftoverCandidate($@"HKCR\{clsidPath}",
                    LeftoverType.RegistryKey, 0.95f, "Exact GUID match in HKCR\\CLSID",
                    Source: "COM registration",
                    RelatedApplication: guid,
                    Location: @"HKCR\CLSID",
                    ProblematicItem: $@"HKCR\{clsidPath}"));
        }
        catch (Exception ex) { log.LogDebug(ex, "Error scanning CLSID for {Guid}", guid); }
    }

    // ── Services ───────────────────────────────────────────────────────────

    private void ScanServices(InstalledApp app, List<LeftoverCandidate> results,
        CancellationToken ct)
    {
        string[] searchNames = BuildSearchNames(app);
        const string servicesPath = @"SYSTEM\CurrentControlSet\Services";

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(servicesPath);
            if (key == null) return;

            foreach (string svcName in key.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                float nameScore = ComputeNameScore(svcName, searchNames, app.Guid);

                // Also check ImagePath
                float pathScore = 0f;
                if (!string.IsNullOrEmpty(app.InstallLocation))
                {
                    using var svcKey = key.OpenSubKey(svcName);
                    string? imgPath  = svcKey?.GetValue("ImagePath") as string ?? string.Empty;
                    if (imgPath.Contains(app.InstallLocation,
                            StringComparison.OrdinalIgnoreCase))
                        pathScore = 0.85f;
                }

                float score = Math.Max(nameScore, pathScore);
                if (score > 0f)
                    results.Add(new LeftoverCandidate(svcName,
                        LeftoverType.Service, score,
                        $"Service matches app name/path",
                        Source: "Windows services",
                        RelatedApplication: app.DisplayName,
                        Location: servicesPath,
                        ProblematicItem: svcName));
            }
        }
        catch (Exception ex) { log.LogDebug(ex, "Error scanning services for {App}", app.DisplayName); }
    }

    // ── Scoring ──────────────────────────────────────────────────────────────

    private static string[] BuildSearchNames(InstalledApp app)
    {
        var names = new List<string>();
        if (!string.IsNullOrEmpty(app.DisplayName)) names.Add(app.DisplayName);
        if (!string.IsNullOrEmpty(app.Publisher))   names.Add(app.Publisher);

        // Tokenise: "Adobe Acrobat Reader" → ["Adobe", "Acrobat", "Reader"]
        foreach (var word in app.DisplayName.Split(' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (word.Length > 3) names.Add(word);

        return [.. names.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static float ComputeNameScore(string candidate, string[] searchNames, string? guid)
    {
        // Exact GUID match
        if (guid != null &&
            candidate.Contains(guid.Trim('{', '}'), StringComparison.OrdinalIgnoreCase))
            return 0.95f;

        float best = 0f;
        foreach (string name in searchNames)
        {
            if (candidate.Equals(name, StringComparison.OrdinalIgnoreCase))
                best = Math.Max(best, 0.85f);
            else if (candidate.Contains(name, StringComparison.OrdinalIgnoreCase))
                best = Math.Max(best, 0.70f);
            else if (Levenshtein(candidate.ToLower(), name.ToLower()) <= 2)
                best = Math.Max(best, 0.50f);
        }
        return best;
    }

    private static string BuildReason(string candidate, InstalledApp app, float score) =>
        score >= 0.90f ? $"מזהה GUID מדויק: {app.Guid}" :
        score >= 0.80f ? $"שם תואם בדיוק: \"{app.DisplayName}\"" :
        score >= 0.65f ? $"שם מכיל: \"{app.Publisher}\"" :
                         $"דמיון שמות גבוה";

    private static int Levenshtein(string s, string t)
    {
        int n = s.Length, m = t.Length;
        if (n == 0) return m;
        if (m == 0) return n;

        // Two-row approach with ArrayPool to avoid (n+1)*(m+1) allocation
        var pool = ArrayPool<int>.Shared;
        int[] prev = pool.Rent(m + 1);
        int[] curr = pool.Rent(m + 1);
        try
        {
            for (int j = 0; j <= m; j++) prev[j] = j;
            for (int i = 1; i <= n; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= m; j++)
                    curr[j] = s[i - 1] == t[j - 1]
                        ? prev[j - 1]
                        : 1 + Math.Min(prev[j - 1], Math.Min(prev[j], curr[j - 1]));
                (prev, curr) = (curr, prev);
            }
            return prev[m];
        }
        finally
        {
            pool.Return(prev);
            pool.Return(curr);
        }
    }

    private long EstimateSize(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (Directory.Exists(path))
                return new DirectoryInfo(path)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);
        }
        catch (Exception ex) { log.LogDebug(ex, "Could not estimate size of {Path}", path); }
        return 0L;
    }
}

// Helper so we don't need to open BaseKey twice
file static class Registry
{
    public static RegistryKey? OpenKey(RegistryHive hive, string path) =>
        hive == RegistryHive.LocalMachine
            ? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path)
            : Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path);

    public static RegistryKey ClassesRoot =>
        Microsoft.Win32.Registry.ClassesRoot;
}
