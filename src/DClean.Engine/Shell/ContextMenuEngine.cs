using DClean.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace DClean.Engine.Shell;

[SupportedOSPlatform("windows")]
public sealed class ContextMenuEngine(ILogger<ContextMenuEngine> log)
{
    // Registry paths that must NEVER be deleted — OS-critical roots
    private static readonly string[] ForbiddenRegistryPrefixes =
    [
        @"HKLM\SYSTEM",
        @"HKLM\SOFTWARE\Microsoft\Windows NT",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers",
        @"HKLM\SAM",
        @"HKLM\SECURITY",
        @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
    ];

    /// <summary>
    /// Validates that a registry entry is within expected shell-extension paths
    /// and not a system-critical key.
    /// </summary>
    private bool IsRegistryPathSafe(ContextMenuEntry entry)
    {
        // Only allow deletions under known shell/SendTo sources
        bool isKnownSource = Sources.Any(s =>
            entry.RegistryPath.StartsWith($@"{s.Root}\{s.Path}", StringComparison.OrdinalIgnoreCase))
            || entry.Type == ContextMenuEntryType.SendTo;

        if (!isKnownSource)
        {
            log.LogWarning("Registry path rejected — not a known shell source: {Path}", entry.RegistryPath);
            return false;
        }

        string fullPath = $@"{entry.RegistryPath}\{entry.SubKeyName}";
        foreach (string forbidden in ForbiddenRegistryPrefixes)
        {
            if (fullPath.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase))
            {
                log.LogWarning("Registry path rejected — matches forbidden prefix: {Path}", fullPath);
                return false;
            }
        }
        return true;
    }

    // Registry roots that host shell extensions / context menu entries
    private static readonly (string Root, string Path, ContextMenuEntryType Type)[] Sources =
    [
        // Classic right-click handlers
        ("HKCR", @"*\shell",                         ContextMenuEntryType.FileShell),
        ("HKCR", @"*\shellex\ContextMenuHandlers",   ContextMenuEntryType.FileShellEx),
        ("HKCR", @"Directory\shell",                 ContextMenuEntryType.FolderShell),
        ("HKCR", @"Directory\shellex\ContextMenuHandlers", ContextMenuEntryType.FolderShellEx),
        ("HKCR", @"Directory\Background\shell",      ContextMenuEntryType.BackgroundShell),
        ("HKCR", @"Directory\Background\shellex\ContextMenuHandlers", ContextMenuEntryType.BackgroundShellEx),
        ("HKCR", @"Drive\shell",                     ContextMenuEntryType.DriveShell),
        ("HKCR", @"AllFilesystemObjects\shellex\ContextMenuHandlers", ContextMenuEntryType.AllFilesShellEx),
        // Send-To folder entries (user-specific shortcuts)
        ("SendTo", string.Empty,                      ContextMenuEntryType.SendTo),
    ];

    // ── Enumerate ─────────────────────────────────────────────────────────────

    public IReadOnlyList<ContextMenuEntry> Enumerate()
    {
        var entries = new List<ContextMenuEntry>();

        foreach (var (root, path, type) in Sources)
        {
            if (root == "SendTo")
            {
                EnumerateSendTo(entries);
                continue;
            }

            using var baseKey = OpenRoot(root);
            if (baseKey == null) continue;

            using var key = baseKey.OpenSubKey(path);
            if (key == null) continue;

            foreach (string subName in key.GetSubKeyNames())
            {
                try
                {
                    using var sub = key.OpenSubKey(subName);
                    if (sub == null) continue;

                    bool isDisabled = subName.StartsWith('-');
                    string displayName = isDisabled ? subName[1..] : subName;

                    // Get MUI verb / display name
                    string? label = sub.GetValue("MUIVerb") as string
                                 ?? sub.GetValue("") as string
                                 ?? displayName;

                    // Get command
                    string? command = null;
                    using (var cmdKey = sub.OpenSubKey("command"))
                        command = cmdKey?.GetValue("") as string;

                    entries.Add(new ContextMenuEntry(
                        Id:           $@"{root}\{path}\{subName}",
                        DisplayName:  label?.Trim() ?? displayName,
                        SubKeyName:   subName,
                        RegistryPath: $@"{root}\{path}",
                        Command:      command,
                        Type:         type,
                        IsEnabled:    !isDisabled));
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "Could not read context menu entry {S}", subName);
                }
            }
        }

        return entries.OrderBy(e => e.Type).ThenBy(e => e.DisplayName).ToList();
    }

    public bool SetEnabled(ContextMenuEntry entry, bool enable)
    {
        if (!IsRegistryPathSafe(entry)) return false;
        try
        {
            using var baseKey = OpenRoot(entry.RegistryPath.Split('\\')[0], writable: true);
            if (baseKey == null) return false;

            string restPath = string.Join('\\', entry.RegistryPath.Split('\\').Skip(1));

            using var parent = baseKey.OpenSubKey(restPath, writable: true);
            if (parent == null) return false;

            string currentName = entry.SubKeyName;
            bool   isDisabled  = currentName.StartsWith('-');

            if (enable && isDisabled)
            {
                // Rename "-name" → "name"
                string newName = currentName[1..];
                CopyKey(parent, currentName, newName);
                parent.DeleteSubKeyTree(currentName);
                log.LogInformation("Enabled context menu: {N}", newName);
            }
            else if (!enable && !isDisabled)
            {
                // Rename "name" → "-name"
                string newName = "-" + currentName;
                CopyKey(parent, currentName, newName);
                parent.DeleteSubKeyTree(currentName);
                log.LogInformation("Disabled context menu: {N}", currentName);
            }
            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SetEnabled failed for {Id}", entry.Id);
            return false;
        }
    }

    public bool DeleteEntry(ContextMenuEntry entry)
    {
        if (!IsRegistryPathSafe(entry)) return false;
        try
        {
            using var baseKey = OpenRoot(entry.RegistryPath.Split('\\')[0], writable: true);
            if (baseKey == null) return false;

            string restPath = string.Join('\\', entry.RegistryPath.Split('\\').Skip(1));
            using var parent = baseKey.OpenSubKey(restPath, writable: true);
            if (parent == null) return false;

            parent.DeleteSubKeyTree(entry.SubKeyName, throwOnMissingSubKey: false);
            log.LogInformation("Deleted context menu entry: {Id}", entry.Id);
            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "DeleteEntry failed for {Id}", entry.Id);
            return false;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static RegistryKey? OpenRoot(string root, bool writable = false) => root switch
    {
        "HKCR" => writable
            ? RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Default)
            : Registry.ClassesRoot,
        "HKLM" => writable
            ? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default)
            : Registry.LocalMachine,
        "HKCU" => writable
            ? RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default)
            : Registry.CurrentUser,
        _      => null
    };

    private void EnumerateSendTo(List<ContextMenuEntry> entries)
    {
        try
        {
            string sendToPath = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
            if (!Directory.Exists(sendToPath)) return;

            foreach (string file in Directory.EnumerateFileSystemEntries(sendToPath))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                entries.Add(new ContextMenuEntry(
                    Id:           file,
                    DisplayName:  name,
                    SubKeyName:   Path.GetFileName(file),
                    RegistryPath: sendToPath,
                    Command:      file,
                    Type:         ContextMenuEntryType.SendTo,
                    IsEnabled:    true));
            }
        }
        catch (Exception ex) { log.LogDebug(ex, "Error scanning SendTo directory"); }
    }

    private static void CopyKey(RegistryKey parent, string srcName, string dstName)
    {
        using var src = parent.OpenSubKey(srcName)!;
        using var dst = parent.CreateSubKey(dstName);

        // Copy values
        foreach (string v in src.GetValueNames())
        {
            var val  = src.GetValue(v, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var kind = src.GetValueKind(v);
            if (val != null) dst.SetValue(v, val, kind);
        }

        // Copy sub-keys recursively
        foreach (string sub in src.GetSubKeyNames())
        {
            using var srcSub = src.OpenSubKey(sub)!;
            using var dstSub = dst.CreateSubKey(sub);
            CopyRegistryKey(srcSub, dstSub);
        }
    }

    private static void CopyRegistryKey(RegistryKey src, RegistryKey dst)
    {
        foreach (string v in src.GetValueNames())
        {
            var val  = src.GetValue(v, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var kind = src.GetValueKind(v);
            if (val != null) dst.SetValue(v, val, kind);
        }
        foreach (string sub in src.GetSubKeyNames())
        {
            using var srcSub = src.OpenSubKey(sub)!;
            using var dstSub = dst.CreateSubKey(sub);
            CopyRegistryKey(srcSub, dstSub);
        }
    }
}
