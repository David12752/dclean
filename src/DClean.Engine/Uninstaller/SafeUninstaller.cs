using DClean.Contracts;
using DClean.Engine.Safety;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace DClean.Engine.Uninstaller;

[SupportedOSPlatform("windows")]
public sealed class SafeUninstaller(PathValidator pathValidator, ILogger<SafeUninstaller> log)
{
    private static readonly HashSet<string> BlockedUninstallHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe",
        "powershell.exe",
        "pwsh.exe",
        "wscript.exe",
        "cscript.exe",
        "mshta.exe",
        "rundll32.exe",
        "regsvr32.exe",
    };

    private static readonly HashSet<string> AllowedBareExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "msiexec.exe",
    };

    // ── Registry snapshot before any destructive action ──────────────────────

    public string CreateRegistrySnapshot(string backupDir)
    {
        Directory.CreateDirectory(backupDir);
        string ts   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string hklm = Path.Combine(backupDir, $"hklm_software_{ts}.reg");
        string hkcu = Path.Combine(backupDir, $"hkcu_software_{ts}.reg");

        RunBlocking("reg.exe", $"export HKLM\\SOFTWARE \"{hklm}\" /y");
        RunBlocking("reg.exe", $"export HKCU\\SOFTWARE \"{hkcu}\" /y");

        log.LogInformation("Registry snapshot saved to {Dir}", backupDir);
        return backupDir;
    }

    // ── Zombie process termination ────────────────────────────────────────────

    public IReadOnlyList<Process> FindZombies(InstalledApp app)
    {
        if (!TryGetScopedInstallLocation(app, out var loc, out var reason))
        {
            log.LogWarning("Skipping zombie scan for {App}: {Reason}", app.DisplayName, reason);
            return [];
        }

        return Process.GetProcesses()
            .Where(p =>
            {
                try
                {
                    string? path = p.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path)) return false;
                    if (!IsSameOrUnder(loc, path)) return false;

                    string processName = p.ProcessName;
                    if (ProcessBlacklist.IsCritical(processName) ||
                        ProcessBlacklist.IsProtected(processName))
                        return false;

                    return !pathValidator.IsSystemProtectedPath(path, out _);
                }
                catch (Exception)
                {
                    return false;
                }
            })
            .ToList();
    }

    public async Task<bool> CloseZombiesAsync(IReadOnlyList<Process> zombies,
        CancellationToken ct = default)
    {
        foreach (var p in zombies)
        {
            try { p.CloseMainWindow(); } catch (Exception ex) { log.LogDebug(ex, "CloseMainWindow failed"); }
        }

        await Task.Delay(3000, ct).ConfigureAwait(false);

        bool allGone = zombies.All(p => { try { return p.HasExited; } catch (Exception) { return true; } });
        return allGone;
    }

    public void ForceKillZombies(IReadOnlyList<Process> zombies)
    {
        foreach (var p in zombies)
        {
            try
            {
                string processName = p.ProcessName;
                string? processPath = p.MainModule?.FileName;
                if (ProcessBlacklist.IsCritical(processName) ||
                    ProcessBlacklist.IsProtected(processName) ||
                    string.IsNullOrWhiteSpace(processPath) ||
                    pathValidator.IsSystemProtectedPath(processPath, out _))
                {
                    log.LogWarning("Skipping zombie kill for protected process {Name} (PID {Pid})",
                        processName, p.Id);
                    continue;
                }

                if (!p.HasExited) p.Kill(entireProcessTree: false);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not kill zombie PID {Pid}", p.Id);
            }
        }
    }

    // ── Standard uninstall execution ──────────────────────────────────────────

    public async Task<(bool Success, int ExitCode, string Detail)> RunUninstallerAsync(
        InstalledApp app, IProgress<UninstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        (string exe, string args) = BuildUninstallCommand(app);

        if (!ValidateUninstallCommand(app, exe, out var validationError))
        {
            log.LogWarning("Blocked uninstall command for {App}: {Reason}", app.DisplayName, validationError);
            return (false, -1, validationError);
        }

        log.LogInformation("Running uninstaller: {Exe} {Args}", exe, args);
        progress?.Report(new UninstallProgress("מריץ את ה-uninstaller הרשמי...", 10));

        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo(exe, args)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };

            proc.Start();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(10));
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            int code = proc.ExitCode;
            bool ok  = code is 0 or 3010; // 3010 = success, reboot required
            string stderr = stderrTask.Result;

            string detail = ok
                ? $"ה-uninstaller סיים (קוד: {code})"
                : $"ה-uninstaller סיים עם שגיאה (קוד: {code})\n{stderr.Trim()}";

            log.LogInformation("Uninstaller exit {Code} for {App}", code, app.DisplayName);
            return (ok, code, detail);
        }
        catch (OperationCanceledException)
        {
            return (false, -1, "הסרת ההתקנה בוטלה");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Uninstaller failed for {App}", app.DisplayName);
            return (false, -1, ex.Message);
        }
    }

    private static (string Exe, string Args) BuildUninstallCommand(InstalledApp app)
    {
        return app.Type switch
        {
            InstallerType.MSI =>
                ("msiexec.exe", $"/x {app.Guid} /qn /norestart"),

            InstallerType.Squirrel =>
                (ExtractExe(app.UninstallString), "--uninstall --silent"),

            InstallerType.InnoSetup =>
                (ExtractExe(app.QuietUninstallString ?? app.UninstallString),
                 "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"),

            InstallerType.NSIS =>
                (ExtractExe(app.QuietUninstallString ?? app.UninstallString), "/S"),

            _ when app.QuietUninstallString is { Length: > 0 } =>
                SplitCommand(app.QuietUninstallString),

            _ => TryBuildSilentCommand(app.UninstallString),
        };
    }

    private static (string Exe, string Args) TryBuildSilentCommand(string uninstallString)
    {
        // Try common silent switches
        (string exe, string existingArgs) = SplitCommand(uninstallString);
        string[] silentTries = ["/SILENT", "/S", "/quiet", "-silent"];
        foreach (var flag in silentTries)
            if (!existingArgs.Contains(flag, StringComparison.OrdinalIgnoreCase))
                return (exe, $"{existingArgs} {flag}".Trim());
        return (exe, existingArgs);
    }

    private static string ExtractExe(string cmdLine)
    {
        cmdLine = cmdLine.Trim();
        if (cmdLine.StartsWith('"'))
        {
            int end = cmdLine.IndexOf('"', 1);
            return end > 1 ? cmdLine[1..end] : cmdLine;
        }
        int space = cmdLine.IndexOf(' ');
        return space > 0 ? cmdLine[..space] : cmdLine;
    }

    private static (string Exe, string Args) SplitCommand(string cmdLine)
    {
        cmdLine = cmdLine.Trim();
        if (cmdLine.StartsWith('"'))
        {
            int end = cmdLine.IndexOf('"', 1);
            if (end > 1)
                return (cmdLine[1..end], cmdLine[(end + 1)..].Trim());
        }
        int space = cmdLine.IndexOf(' ');
        return space > 0
            ? (cmdLine[..space], cmdLine[(space + 1)..])
            : (cmdLine, string.Empty);
    }

    private bool ValidateUninstallCommand(InstalledApp app, string exe, out string reason)
    {
        reason = string.Empty;

        string fileName = Path.GetFileName(exe);
        if (BlockedUninstallHosts.Contains(fileName))
        {
            reason = $"חסמתי uninstaller מסוכן ({fileName}). נדרשת בדיקה ידנית.";
            return false;
        }

        if (AllowedBareExecutables.Contains(fileName) &&
            string.Equals(exe, fileName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!Path.IsPathFullyQualified(exe))
        {
            reason = $"פקודת uninstall לא בטוחה או לא מלאה: {exe}";
            return false;
        }

        try
        {
            string fullExe = Path.GetFullPath(exe);
            if (pathValidator.IsSystemProtectedPath(fullExe, out var protectionReason) &&
                !string.Equals(fileName, "msiexec.exe", StringComparison.OrdinalIgnoreCase))
            {
                reason = $"קובץ ה-uninstaller נמצא באזור מוגן: {protectionReason}";
                return false;
            }

            if (!TryGetScopedInstallLocation(app, out var location, out _) ||
                !IsSameOrUnder(location, fullExe))
            {
                string normalizedName = fileName.ToLowerInvariant();
                if (!(normalizedName.StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||
                      normalizedName is "uninstall.exe" or "setup.exe" or "update.exe"))
                {
                    reason = "קובץ ה-uninstaller אינו נמצא בתיקיית היישום ולכן נחסם מטעמי בטיחות.";
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            reason = $"לא ניתן לאמת את קובץ ה-uninstaller: {ex.Message}";
            return false;
        }

        return true;
    }

    private bool TryGetScopedInstallLocation(InstalledApp app, out string fullLocation, out string reason)
    {
        fullLocation = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(app.InstallLocation))
        {
            reason = "Install location is missing";
            return false;
        }

        try
        {
            fullLocation = Path.GetFullPath(app.InstallLocation)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex)
        {
            reason = $"Install location is invalid: {ex.Message}";
            return false;
        }

        string driveRoot = Path.GetPathRoot(fullLocation)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty;
        string[] overlyBroadRoots =
        [
            driveRoot,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        ];

        string normalizedLocation = fullLocation;
        if (overlyBroadRoots
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Any(root => string.Equals(root, normalizedLocation, StringComparison.OrdinalIgnoreCase)))
        {
            reason = "Install location is too broad for safe process targeting";
            return false;
        }

        return true;
    }

    private static bool IsSameOrUnder(string rootPath, string candidatePath)
    {
        string root = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string full = Path.GetFullPath(candidatePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(root, full, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // ── Leftover deletion ─────────────────────────────────────────────────────

    public async Task<(int Deleted, long BytesFreed)> DeleteLeftoversAsync(
        IReadOnlyList<LeftoverCandidate> candidates,
        string backupDir,
        IProgress<UninstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        int deleted = 0;
        long freed  = 0L;
        int total   = candidates.Count;

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = candidates[i];
            int pct = 60 + (int)((float)(i + 1) / total * 40);

            try
            {
                switch (c.Type)
                {
                    case LeftoverType.Directory:
                        if (Directory.Exists(c.Path))
                        {
                            if (pathValidator.IsSystemProtectedPath(c.Path, out var dirReason))
                            {
                                log.LogWarning("Leftover dir blocked: {Path} — {Reason}", c.Path, dirReason);
                                break;
                            }
                            progress?.Report(new UninstallProgress($"מוחק תיקייה: {c.Path}", pct));
                            freed += DeleteDirectorySafely(c.Path);
                            deleted++;
                        }
                        break;

                    case LeftoverType.File:
                        if (File.Exists(c.Path))
                        {
                            if (pathValidator.IsSystemProtectedPath(c.Path, out var fileReason))
                            {
                                log.LogWarning("Leftover file blocked: {Path} — {Reason}", c.Path, fileReason);
                                break;
                            }
                            progress?.Report(new UninstallProgress($"מוחק קובץ: {c.Path}", pct));
                            if (TryDeleteFile(c.Path, out long fileFreed))
                            {
                                freed += fileFreed;
                                deleted++;
                            }
                        }
                        break;

                    case LeftoverType.RegistryKey:
                        progress?.Report(new UninstallProgress($"מוחק מפתח: {c.Path}", pct));
                        SafeDeleteRegistryKey(c.Path, backupDir);
                        deleted++;
                        break;

                    case LeftoverType.Service:
                        progress?.Report(new UninstallProgress($"מסיר שירות: {c.Path}", pct));
                        await RunAsyncBlocking("sc.exe", $"delete \"{c.Path}\"", ct).ConfigureAwait(false);
                        deleted++;
                        break;
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not delete leftover {Path}", c.Path);
            }
        }

        return (deleted, freed);
    }

    private void SafeDeleteRegistryKey(string fullPath, string backupDir)
    {
        // Parse "HKLM\SOFTWARE\Foo" → hive + subkey
        int slash = fullPath.IndexOf('\\');
        if (slash < 0) return;

        string hivePart = fullPath[..slash].ToUpperInvariant();
        string subPath  = fullPath[(slash + 1)..];

        RegistryKey? baseKey = hivePart switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE"  => Microsoft.Win32.Registry.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER"   => Microsoft.Win32.Registry.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT"   => Microsoft.Win32.Registry.ClassesRoot,
            _ => null
        };
        if (baseKey == null) return;

        // Export backup first
        string safeName = Regex.Replace(subPath, @"[\\/:*?""<>|]", "_");
        string backup   = Path.Combine(backupDir, $"reg_{safeName}.reg");
        RunBlocking("reg.exe", $"export \"{fullPath}\" \"{backup}\" /y");

        // Split into parent + leaf
        int lastSlash  = subPath.LastIndexOf('\\');
        string parent  = lastSlash > 0 ? subPath[..lastSlash] : string.Empty;
        string leaf    = lastSlash > 0 ? subPath[(lastSlash + 1)..] : subPath;

        using var parentKey = string.IsNullOrEmpty(parent)
            ? baseKey
            : baseKey.OpenSubKey(parent, writable: true);

        parentKey?.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
        log.LogInformation("Deleted registry key {Path}", fullPath);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void RunBlocking(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
                { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(10_000);
        }
        catch (Exception ex) { log.LogDebug(ex, "RunBlocking failed: {Exe} {Args}", exe, args); }
    }

    private async Task RunAsyncBlocking(string exe, string args, CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
                { UseShellExecute = false, CreateNoWindow = true })!;
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) { log.LogDebug(ex, "RunAsyncBlocking failed: {Exe} {Args}", exe, args); }
    }

    private long DeleteDirectorySafely(string rootPath)
    {
        long freed = 0;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
                .OrderByDescending(p => p.Length)
                .ToList();
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not enumerate files in {Path}", rootPath);
            files = [];
        }

        foreach (string file in files)
        {
            if (!IsWithinRoot(rootPath, file)) continue;
            if (pathValidator.IsSystemProtectedPath(file, out var reason))
            {
                log.LogWarning("Skipping protected file inside leftover dir: {Path} — {Reason}", file, reason);
                continue;
            }

            if (TryDeleteFile(file, out long fileFreed))
                freed += fileFreed;
        }

        try
        {
            foreach (string dir in Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories)
                .OrderByDescending(p => p.Length))
            {
                if (!IsWithinRoot(rootPath, dir)) continue;
                if (pathValidator.IsSystemProtectedPath(dir, out _)) continue;
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir, recursive: false);
                }
                catch (Exception ex) { log.LogDebug(ex, "Could not delete empty dir {Dir}", dir); }
            }

            if (!Directory.EnumerateFileSystemEntries(rootPath).Any())
                Directory.Delete(rootPath, recursive: false);
        }
        catch (Exception ex) { log.LogDebug(ex, "Error cleaning directories in {Path}", rootPath); }

        return freed;
    }

    private bool TryDeleteFile(string path, out long bytesFreed)
    {
        bytesFreed = 0;
        try
        {
            var info = new FileInfo(path);
            bytesFreed = info.Exists ? info.Length : 0;
            info.Delete();
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not delete file {Path}", path);
            return false;
        }
    }

    private static bool IsWithinRoot(string rootPath, string candidatePath)
    {
        return IsSameOrUnder(rootPath, candidatePath);
    }

    private long EstimateDirectorySize(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch (Exception ex) { log.LogDebug(ex, "Could not estimate size of {Path}", path); return 0L; }
    }
}
