# DClean — Part 2: Granular Project Plan
## Phase 2: The Cleaning Engine

---

## Phase 2.1 — The Safety Gate (PathValidator)

This is the most important class in the entire codebase. Every path that could be deleted MUST pass through it first. It runs synchronously, before any async I/O.

```csharp
// DClean.Engine/Safety/PathValidator.cs

public enum ValidationResult { Allowed, Blocked }

public sealed class PathValidator
{
    // ── Hardcoded absolute path prefixes that can NEVER be touched ──────────
    private static readonly string[] _forbiddenPrefixes = new[]
    {
        @"C:\Windows\System32",
        @"C:\Windows\SysWOW64",
        @"C:\Windows\WinSxS",        // Component store — modifying = corruption
        @"C:\Windows\servicing",
        @"C:\Windows\Boot",
        @"C:\Windows\Fonts",
        @"C:\Windows\INF",
        @"C:\Windows\assembly",      // GAC
        @"C:\Windows\Microsoft.NET",
        @"C:\Windows\SystemApps",
        @"C:\Windows\ImmersiveControlPanel",
        @"C:\Program Files",         // NEVER auto-delete from here
        @"C:\Program Files (x86)",
        @"C:\ProgramData\Microsoft\Windows\Start Menu",
        @"C:\Users\Default",
        @"C:\Recovery",
        @"C:\$Recycle.Bin",
        @"C:\$WinREAgent",
        @"C:\System Volume Information",
        @"C:\bootmgr",
        @"C:\BOOTNXT",
        @"C:\pagefile.sys",
        @"C:\hiberfil.sys",
        @"C:\swapfile.sys",
    };

    // ── Hardcoded filename patterns that are always blocked ─────────────────
    private static readonly string[] _forbiddenFilenames = new[]
    {
        "ntldr", "bootmgr", "boot.ini", "ntdetect.com",
        "desktop.ini",    // Silently skipping these prevents shell corruption
        "thumbs.db",      // No space savings, high risk of shell lock
    };

    // ── File extensions that are NEVER deleted regardless of location ────────
    private static readonly HashSet<string> _forbiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".drv", ".ocx", ".cpl",
        ".msi", ".cat", ".inf", ".lnk", ".hlp",
    };

    // ── The positive allowlist — ONLY these directory roots are candidates ───
    // Resolved at runtime using Environment.GetFolderPath / SHGetKnownFolderPath
    private readonly HashSet<string> _allowedRoots;

    private readonly ILogger<PathValidator> _log;

    public PathValidator(ILogger<PathValidator> log)
    {
        _log = log;
        _allowedRoots = BuildAllowedRoots();
    }

    private static HashSet<string> BuildAllowedRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Per-user temp
        AddIfExists(roots, Path.GetTempPath());
        // System temp
        AddIfExists(roots, @"C:\Windows\Temp");
        // Per-user AppData\Local\Temp (same as GetTempPath() for most users, but explicit)
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AddIfExists(roots, Path.Combine(localAppData, "Temp"));
        // Browser caches — populated dynamically by BrowserCacheLocator
        // (Added via AddAllowedRoot() at runtime)
        return roots;
    }

    public void AddAllowedRoot(string path) => _allowedRoots.Add(
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar));

    private static void AddIfExists(HashSet<string> set, string path)
    {
        if (Directory.Exists(path))
            set.Add(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar));
    }

    public ValidationResult Validate(string rawPath, out string reason)
    {
        reason = string.Empty;

        // ── 1. Normalize: resolve symlinks/junctions to real path ────────────
        //    This is the critical defense against symlink-traversal attacks.
        string fullPath;
        try
        {
            // GetFullPath does NOT resolve symlinks; we need the real path.
            fullPath = GetRealPath(rawPath);
        }
        catch (Exception ex)
        {
            reason = $"Path normalization failed: {ex.Message}";
            DCleanEventSource.Log.SafetyGateBlocked(rawPath, reason);
            return ValidationResult.Blocked;
        }

        // ── 2. Block forbidden prefixes ──────────────────────────────────────
        foreach (var prefix in _forbiddenPrefixes)
        {
            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Forbidden prefix: {prefix}";
                DCleanEventSource.Log.SafetyGateBlocked(fullPath, reason);
                return ValidationResult.Blocked;
            }
        }

        // ── 3. Block forbidden extensions ────────────────────────────────────
        var ext = Path.GetExtension(fullPath);
        if (_forbiddenExtensions.Contains(ext))
        {
            reason = $"Forbidden extension: {ext}";
            DCleanEventSource.Log.SafetyGateBlocked(fullPath, reason);
            return ValidationResult.Blocked;
        }

        // ── 4. Block forbidden filenames ─────────────────────────────────────
        var filename = Path.GetFileName(fullPath);
        if (_forbiddenFilenames.Any(f => string.Equals(f, filename, StringComparison.OrdinalIgnoreCase)))
        {
            reason = $"Forbidden filename: {filename}";
            DCleanEventSource.Log.SafetyGateBlocked(fullPath, reason);
            return ValidationResult.Blocked;
        }

        // ── 5. Allowlist check — path must descend from an allowed root ───────
        bool inAllowedRoot = _allowedRoots.Any(root =>
            fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));

        if (!inAllowedRoot)
        {
            reason = "Path is not under any allowed root";
            DCleanEventSource.Log.SafetyGateBlocked(fullPath, reason);
            return ValidationResult.Blocked;
        }

        return ValidationResult.Allowed;
    }

    /// <summary>
    /// Resolves symlinks and junctions to the canonical real path.
    /// Uses DeviceIoControl(FSCTL_GET_REPARSE_POINT) via CsWin32 for junction detection,
    /// then Path.GetFullPath for the rest. Falls back to FileInfo.LinkTarget on .NET 6+.
    /// </summary>
    private static string GetRealPath(string path)
    {
        // .NET 6+ exposes ResolveLinkTarget(returnFinalTarget: true) which walks
        // the entire chain (symlink → symlink → real file) in one call.
        var info = new FileInfo(path);
        if (info.Exists)
        {
            var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved != null)
                return Path.GetFullPath(resolved.FullName);
        }
        else
        {
            var dir = new DirectoryInfo(path);
            if (dir.Exists)
            {
                var resolved = dir.ResolveLinkTarget(returnFinalTarget: true);
                if (resolved != null)
                    return Path.GetFullPath(resolved.FullName);
            }
        }
        return Path.GetFullPath(path);
    }
}
```

### Critical Edge Case: Symlinks & Junctions

`C:\Users\Username\AppData\Local\Temp` may itself be a symlink or junction point redirecting to `C:\Windows\System32\...`. The validator always calls `ResolveLinkTarget(returnFinalTarget: true)` **before** prefix-checking. Without this, an attacker or a broken installer could create a malicious junction inside `%TEMP%` pointing to `System32`, and a naive `Path.StartsWith(tempPath)` check would not catch it. This is a well-documented attack vector in Windows privilege escalation (T1574.010 in MITRE ATT&CK).

---

## Phase 2.2 — Safe Paths Allowlist (BrowserCacheLocator)

```csharp
// DClean.Engine/Cleaning/BrowserCacheLocator.cs

public sealed class BrowserCacheLocator
{
    private readonly string _localAppData;
    private readonly string _roamingAppData;
    private readonly ILogger<BrowserCacheLocator> _log;

    public BrowserCacheLocator(ILogger<BrowserCacheLocator> log)
    {
        _log = log;
        _localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    }

    /// <summary>Returns discovered cache directories that actually exist on this system.</summary>
    public IReadOnlyList<CacheTarget> Discover()
    {
        var targets = new List<CacheTarget>();

        // Each entry: (browser name, subpath under %LOCALAPPDATA%, estimated safe-to-delete subfolder)
        var candidates = new (string Browser, string SubPath)[]
        {
            ("Google Chrome",  @"Google\Chrome\User Data\Default\Cache"),
            ("Google Chrome",  @"Google\Chrome\User Data\Default\Code Cache"),
            ("Google Chrome",  @"Google\Chrome\User Data\Default\GPUCache"),
            ("MS Edge",        @"Microsoft\Edge\User Data\Default\Cache"),
            ("MS Edge",        @"Microsoft\Edge\User Data\Default\Code Cache"),
            ("Firefox",        @"Mozilla\Firefox\Profiles"),   // Special: must enumerate profiles
            ("Brave",          @"BraveSoftware\Brave-Browser\User Data\Default\Cache"),
            ("Opera",          @"Opera Software\Opera Stable\Cache"),
            ("Vivaldi",        @"Vivaldi\User Data\Default\Cache"),
        };

        foreach (var (browser, subpath) in candidates)
        {
            if (browser == "Firefox")
            {
                // Firefox uses profile directories — enumerate them
                var profilesRoot = Path.Combine(_roamingAppData, subpath);
                if (Directory.Exists(profilesRoot))
                {
                    foreach (var profile in Directory.GetDirectories(profilesRoot))
                    {
                        var cachePath = Path.Combine(
                            _localAppData, "Mozilla", "Firefox", "Profiles",
                            Path.GetFileName(profile), "cache2");
                        if (Directory.Exists(cachePath))
                            targets.Add(new CacheTarget(browser, cachePath,
                                estimatedBytes: EstimateSize(cachePath)));
                    }
                }
            }
            else
            {
                var full = Path.Combine(_localAppData, subpath);
                if (Directory.Exists(full))
                    targets.Add(new CacheTarget(browser, full, EstimateSize(full)));
            }
        }

        return targets;
    }

    private static long EstimateSize(string dir)
    {
        try
        {
            return new DirectoryInfo(dir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch { return -1; }
    }
}

public record CacheTarget(string BrowserName, string FullPath, long EstimatedBytes);
```

---

## Phase 2.3 — FileScanner (Async, Cancelable)

```csharp
// DClean.Engine/Cleaning/FileScanner.cs

public sealed class FileScanner
{
    private readonly PathValidator _validator;
    private readonly ILogger<FileScanner> _log;

    public FileScanner(PathValidator validator, ILogger<FileScanner> log)
    {
        _validator = validator;
        _log = log;
    }

    /// <summary>
    /// Asynchronously enumerates files under the given roots and reports findings.
    /// Uses IAsyncEnumerable so the UI can stream progress without buffering all results.
    /// </summary>
    public async IAsyncEnumerable<ScanResult> ScanAsync(
        IEnumerable<string> roots,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var root in roots)
        {
            var enumOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,    // Skip Access Denied silently
                AttributesToSkip = FileAttributes.System | FileAttributes.Device,
                ReturnSpecialDirectories = false,
            };

            IEnumerable<FileInfo> files;
            try
            {
                files = new DirectoryInfo(root).EnumerateFiles("*", enumOptions);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Cannot enumerate {Root}: {Msg}", root, ex.Message);
                continue;
            }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                // ── Reparse point guard ──────────────────────────────────────
                // Never follow reparse points (symlinks/junctions) during traversal.
                // EnumerationOptions.AttributesToSkip does NOT filter ReparsePoints
                // by default in all .NET versions, so we check explicitly.
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    _log.LogDebug("Skipping reparse point: {Path}", file.FullName);
                    continue;
                }

                if (_validator.Validate(file.FullName, out var reason) == ValidationResult.Blocked)
                {
                    yield return new ScanResult(file.FullName, file.Length, Blocked: true, reason);
                    continue;
                }

                yield return new ScanResult(file.FullName, file.Length, Blocked: false, string.Empty);

                // Yield control to avoid starving the pipe server's message loop
                await Task.Yield();
            }
        }
    }
}

public record ScanResult(string Path, long SizeBytes, bool Blocked, string BlockReason);
```

---

## Phase 2.4 — SafeDeleter with Locked-File Handling

```csharp
// DClean.Engine/Cleaning/SafeDeleter.cs
// P/Invoke: MoveFileEx (MOVEFILE_DELAY_UNTIL_REBOOT) for locked files

public sealed class SafeDeleter
{
    private const int ERROR_SHARING_VIOLATION = 32;
    private const int ERROR_LOCK_VIOLATION    = 33;
    private const int ERROR_ACCESS_DENIED     = 5;

    private readonly PathValidator _validator;
    private readonly DryRunGuard _dryRunGuard;
    private readonly ILogger<SafeDeleter> _log;

    public SafeDeleter(PathValidator v, DryRunGuard g, ILogger<SafeDeleter> log)
    {
        _validator = v; _dryRunGuard = g; _log = log;
    }

    public DeleteOutcome Delete(string path)
    {
        // ── Always re-validate, even if the scanner already checked ──────────
        if (_validator.Validate(path, out var reason) == ValidationResult.Blocked)
            return new DeleteOutcome(path, Status.Blocked, reason, 0);

        long size = 0;
        try { size = new FileInfo(path).Length; } catch { }

        if (!_dryRunGuard.IsExecuteMode)
        {
            _log.LogInformation("[DRY-RUN] Would delete: {Path} ({Size} bytes)", path, size);
            return new DeleteOutcome(path, Status.DryRun, string.Empty, size);
        }

        // ── Actual deletion ──────────────────────────────────────────────────
        try
        {
            File.Delete(path);
            DCleanEventSource.Log.FileScanEntry(path, size); // reuse for deletion audit
            return new DeleteOutcome(path, Status.Deleted, string.Empty, size);
        }
        catch (IOException ioEx) when (IsLockError(ioEx))
        {
            // File is locked by another process.
            // Strategy: schedule deletion on next reboot via MoveFileEx.
            return ScheduleDeleteOnReboot(path, size);
        }
        catch (UnauthorizedAccessException uaEx)
        {
            _log.LogWarning("Access denied deleting {Path}: {Msg}", path, uaEx.Message);
            return new DeleteOutcome(path, Status.AccessDenied, uaEx.Message, 0);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unexpected error deleting {Path}", path);
            return new DeleteOutcome(path, Status.Error, ex.Message, 0);
        }
    }

    private static bool IsLockError(IOException ex)
    {
        int hResult = ex.HResult & 0xFFFF;
        return hResult is ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION;
    }

    private DeleteOutcome ScheduleDeleteOnReboot(string path, long size)
    {
        // MoveFileEx with null destination = delete on reboot.
        // Requires SeBackupPrivilege in some scenarios; LocalSystem already has it.
        bool success = PInvoke.MoveFileEx(path, null,
            MOVEFILE_FLAGS.MOVEFILE_DELAY_UNTIL_REBOOT);

        if (success)
        {
            _log.LogWarning("File locked; scheduled for deletion on reboot: {Path}", path);
            return new DeleteOutcome(path, Status.ScheduledReboot, string.Empty, size);
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            _log.LogError("MoveFileEx reboot-schedule failed for {Path}: Win32={Error}", path, err);
            return new DeleteOutcome(path, Status.Error, $"Win32 error {err}", 0);
        }
    }
}

public enum Status { DryRun, Deleted, Blocked, AccessDenied, ScheduledReboot, Error }
public record DeleteOutcome(string Path, Status Status, string Detail, long BytesFreed);
```

### Edge Case: `ERROR_SHARING_VIOLATION` on Log Files

Many applications lock their own log files with `FILE_SHARE_READ` but not `FILE_SHARE_DELETE`. The `MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)` approach is the correct pattern — it writes a `PendingFileRenameOperations` value under `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager`. This is the exact same mechanism Windows Update uses.

**Warning:** Never call `MoveFileEx` on anything not in the allowlist. A `PendingFileRenameOperations` entry for a System32 DLL will brick the OS on next boot. The `PathValidator.Validate()` call at the top of `Delete()` is therefore not optional — it is a safety contract.

---

## Phase 2.5 — Windows Temp Folder Special Handling

`C:\Windows\Temp` and `%TEMP%` require extra care:

1. **Age filter:** Only delete files not modified in the last **48 hours**. Deleting fresh temp files can corrupt in-progress installations.
2. **Installer lock detection:** Check if a file is named like a Windows Installer temp file (`MSI*.tmp`) — skip these unconditionally.
3. **Directory deletion order:** Always delete files before directories. Use `Directory.Delete(path, recursive: false)` only after confirming the directory is empty, rather than `recursive: true`, which bypasses per-file validation.

```csharp
private static bool IsTooRecent(FileInfo file) =>
    (DateTime.UtcNow - file.LastWriteTimeUtc).TotalHours < 48;

private static bool IsMsiTempFile(FileInfo file) =>
    file.Name.StartsWith("MSI", StringComparison.OrdinalIgnoreCase) &&
    string.Equals(file.Extension, ".tmp", StringComparison.OrdinalIgnoreCase);
```
