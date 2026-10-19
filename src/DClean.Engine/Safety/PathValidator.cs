using DClean.Engine.Logging;
using Microsoft.Extensions.Logging;

namespace DClean.Engine.Safety;

public enum ValidationResult { Allowed, Blocked }

public sealed class PathValidator
{
    private static readonly string[] CriticalForbiddenPrefixes = BuildCriticalForbiddenPrefixes();
    private static readonly string[] CleanerForbiddenPrefixes = BuildCleanerForbiddenPrefixes();

    private static string[] BuildCriticalForbiddenPrefixes()
    {
        string winDir   = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string sys32    = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string sysWow   = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        string drive     = Path.GetPathRoot(winDir) ?? @"C:\";

        var list = new List<string>
        {
            sys32,
            sysWow,
            Path.Combine(winDir, "WinSxS"),
            Path.Combine(winDir, "servicing"),
            Path.Combine(winDir, "Boot"),
            Path.Combine(winDir, "Fonts"),
            Path.Combine(winDir, "INF"),
            Path.Combine(winDir, "assembly"),
            Path.Combine(winDir, "Microsoft.NET"),
            Path.Combine(winDir, "SystemApps"),
            Path.Combine(winDir, "ImmersiveControlPanel"),
            Path.Combine(winDir, "Panther"),
            Path.Combine(winDir, "SoftwareDistribution", "Download"),
            Path.Combine(winDir, "LiveKernelReports"),
            Path.Combine(winDir, "Minidump"),
            Path.Combine(drive, "Recovery"),
            Path.Combine(drive, "$Recycle.Bin"),
            Path.Combine(drive, "$WinREAgent"),
            Path.Combine(drive, "System Volume Information"),
            Path.Combine(drive, "EFI"),
        };

        return list
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] BuildCleanerForbiddenPrefixes()
    {
        string progFiles     = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string progFilesX86  = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string commonApp     = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string usersRoot     = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                               ?? @"C:\Users";

        return [
            .. CriticalForbiddenPrefixes,
            Path.GetFullPath(progFiles).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(progFilesX86).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(Path.Combine(usersRoot, "Default")).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(Path.Combine(usersRoot, "Public", "Desktop")).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(Path.Combine(commonApp, "Microsoft", "Windows", "Start Menu")).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(Path.Combine(commonApp, "Microsoft", "Windows Defender")).TrimEnd(Path.DirectorySeparatorChar),
        ];
    }

    private static readonly string[] ForbiddenFilenames =
    [
        "ntldr", "bootmgr", "BOOTNXT", "boot.ini", "ntdetect.com",
        "pagefile.sys", "hiberfil.sys", "swapfile.sys",
        "DumpStack.log.tmp",
        "desktop.ini", "thumbs.db",
    ];

    private static readonly HashSet<string> ForbiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".drv", ".ocx", ".cpl",
        ".msi", ".cat", ".inf", ".lnk", ".hlp",
    };

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

        // Explicitly safe cleaning roots only.
        TryAdd(roots, Path.GetTempPath());
        TryAdd(roots, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"));
        var local   = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        TryAdd(roots, Path.Combine(local, "Temp"));
        TryAdd(roots, Path.Combine(local, "CrashDumps"));
        TryAdd(roots, Path.Combine(local, "Microsoft", "Windows", "INetCache"));
        TryAdd(roots, Path.Combine(local, "Microsoft", "Windows", "WebCache"));

        return roots;
    }

    private static void TryAdd(HashSet<string> set, string path)
    {
        try
        {
            set.Add(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar));
        }
        catch { /* ignore invalid paths */ }
    }

    public void AddAllowedRoot(string path)
    {
        try
        {
            _allowedRoots.Add(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar));
        }
        catch { }
    }

    public ValidationResult Validate(string rawPath, out string reason)
    {
        if (TryValidateCore(rawPath, CleanerForbiddenPrefixes, blockForbiddenExtensions: true,
            out var fullPath, out reason) == ValidationResult.Blocked)
            return ValidationResult.Blocked;

        // Allowlist: path must be under an approved root
        bool inAllowedRoot = false;
        foreach (var root in _allowedRoots)
        {
            if (IsSameOrUnderPath(fullPath, root))
            {
                inAllowedRoot = true;
                break;
            }
        }

        if (!inAllowedRoot)
        {
            reason = "Path is not under any allowed root";
            DCleanEventSource.Log.SafetyGateBlocked(fullPath, reason);
            return ValidationResult.Blocked;
        }

        return ValidationResult.Allowed;
    }

    public bool IsSystemProtectedPath(string rawPath, out string reason) =>
        TryValidateCore(rawPath, CriticalForbiddenPrefixes, blockForbiddenExtensions: false,
            out _, out reason) == ValidationResult.Blocked;

    private static ValidationResult TryValidateCore(
        string rawPath,
        IEnumerable<string> forbiddenPrefixes,
        bool blockForbiddenExtensions,
        out string fullPath,
        out string reason)
    {
        reason = string.Empty;
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            reason = "Empty path";
            return ValidationResult.Blocked;
        }

        if (rawPath.StartsWith(@"\\") || rawPath.StartsWith("//"))
        {
            reason = "UNC/network paths are not supported";
            DCleanEventSource.Log.SafetyGateBlocked(rawPath, reason);
            return ValidationResult.Blocked;
        }

        try
        {
            fullPath = GetRealPath(rawPath);
        }
        catch (Exception ex)
        {
            reason = $"Path resolution failed: {ex.Message}";
            DCleanEventSource.Log.SafetyGateBlocked(rawPath, reason);
            return ValidationResult.Blocked;
        }

        foreach (var prefix in forbiddenPrefixes)
        {
            if (IsSameOrUnderPath(fullPath, prefix))
            {
                reason = $"Forbidden prefix: {prefix}";
                DCleanEventSource.Log.SafetyGateBlocked(fullPath, reason);
                return ValidationResult.Blocked;
            }
        }

        if (blockForbiddenExtensions)
        {
            var ext = Path.GetExtension(fullPath);
            if (!string.IsNullOrEmpty(ext) && ForbiddenExtensions.Contains(ext))
            {
                reason = $"Forbidden extension: {ext}";
                DCleanEventSource.Log.SafetyGateBlocked(fullPath, reason);
                return ValidationResult.Blocked;
            }
        }

        var filename = Path.GetFileName(fullPath);
        foreach (var f in ForbiddenFilenames)
        {
            if (string.Equals(f, filename, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Forbidden filename: {filename}";
                DCleanEventSource.Log.SafetyGateBlocked(fullPath, reason);
                return ValidationResult.Blocked;
            }
        }

        return ValidationResult.Allowed;
    }

    private static bool IsSameOrUnderPath(string path, string root)
    {
        string normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        string rootedPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRealPath(string path)
    {
        var fi = new FileInfo(path);
        if (fi.Exists)
        {
            var resolved = fi.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved != null) return Path.GetFullPath(resolved.FullName);
        }
        else
        {
            var di = new DirectoryInfo(path);
            if (di.Exists)
            {
                var resolved = di.ResolveLinkTarget(returnFinalTarget: true);
                if (resolved != null) return Path.GetFullPath(resolved.FullName);
            }
        }
        return Path.GetFullPath(path);
    }
}
