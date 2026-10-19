using DClean.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Diagnostics;
using System.Security;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace DClean.Engine.Startup;

[SupportedOSPlatform("windows")]
public sealed class StartupEngine(ILogger<StartupEngine> log)
{
    // ── Constants ──────────────────────────────────────────────────────────────
    private const int CliTimeoutMs     = 5_000;
    private const int TaskQueryTimeout = 10_000;
    private const int TaskCreateTimeout = 10_000;
    private const int ServiceStartAuto     = 2;  // SCM Auto
    private const int ServiceStartManual   = 3;  // SCM Manual
    private const int ServiceStartDisabled = 4;  // SCM Disabled

    private static readonly string[] RunPaths =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
    ];
    private static readonly string[] RunOncePaths =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
    ];

    // ── Discovery ─────────────────────────────────────────────────────────────

    public IReadOnlyList<StartupItem> Discover()
    {
        var items = new List<StartupItem>();
        ReadRunKeys(items);
        ReadStartupFolders(items);
        ReadScheduledTasks(items);
        ReadServices(items);
        return items.OrderBy(i => i.Name).ToList();
    }

    private void ReadRunKeys(List<StartupItem> items)
    {
        var sources = new[]
        {
            (Registry.LocalMachine, RunPaths[0],  StartupItemSource.RegistryRunHklm, false),
            (Registry.LocalMachine, RunPaths[1],  StartupItemSource.RegistryRunHklm, false),
            (Registry.CurrentUser,  RunPaths[0],  StartupItemSource.RegistryRunHkcu, false),
            (Registry.LocalMachine, RunOncePaths[0], StartupItemSource.RegistryRunOncHklm, false),
            (Registry.CurrentUser,  RunOncePaths[0], StartupItemSource.RegistryRunOnceHkcu, false),
        };

        foreach (var (hive, path, source, _) in sources)
        {
            // Check both enabled and disabled (_-prefixed) keys
            foreach (bool disabled in new[] { false, true })
            {
                string keyPath = disabled
                    ? path.Replace(@"\Run", @"\Run (disabled)")
                    : path;
                try
                {
                    using var key = hive.OpenSubKey(keyPath);
                    if (key == null) continue;
                    foreach (string name in key.GetValueNames())
                    {
                        string cmd = key.GetValue(name)?.ToString() ?? string.Empty;
                        items.Add(new StartupItem(
                            Name:         name,
                            Command:      cmd,
                            Source:       source,
                            Status:       disabled ? StartupItemStatus.Disabled : StartupItemStatus.Enabled,
                            Publisher:    GetPublisher(cmd),
                            RegistryPath: $@"{(hive == Registry.LocalMachine ? "HKLM" : "HKCU")}\{keyPath}\{name}",
                            DelayMinutes: 0));
                    }
                }
                catch (Exception ex) { log.LogDebug(ex, "Error reading registry key {Path}", keyPath); }
            }
        }
    }

    private static void ReadStartupFolders(List<StartupItem> items)
    {
        string userStartup = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Start Menu\Programs\Startup");
        string commonStartup = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Microsoft\Windows\Start Menu\Programs\Startup");

        foreach (var (folder, source) in new[]
        {
            (userStartup,   StartupItemSource.StartupFolderUser),
            (commonStartup, StartupItemSource.StartupFolderCommon),
        })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (string file in Directory.EnumerateFiles(folder, "*.lnk"))
            {
                items.Add(new StartupItem(
                    Name:         Path.GetFileNameWithoutExtension(file),
                    Command:      file,
                    Source:       source,
                    Status:       StartupItemStatus.Enabled,
                    Publisher:    string.Empty,
                    RegistryPath: null,
                    DelayMinutes: 0));
            }
        }
    }

    private void ReadScheduledTasks(List<StartupItem> items)
    {
        try
        {
            // Use schtasks.exe /query — avoids COM TaskScheduler interop dependency
            using var proc = Process.Start(new ProcessStartInfo("schtasks.exe",
                "/query /fo CSV /nh /v") {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                CreateNoWindow         = true
            })!;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(TaskQueryTimeout);

            // Parse CSV: fields include TaskName, Status, Last Run Time, Next Run Time, Logon Time
            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // Only logon/boot triggered tasks
                if (!line.Contains("At logon", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("At startup", StringComparison.OrdinalIgnoreCase)) continue;

                string[] cols = SplitCsv(line);
                if (cols.Length < 2) continue;

                string taskName = cols[0].Trim('"', ' ');
                string status   = cols.Length > 3 ? cols[3].Trim('"', ' ') : "Unknown";

                items.Add(new StartupItem(
                    Name:         Path.GetFileName(taskName),
                    Command:      taskName,
                    Source:       StartupItemSource.ScheduledTask,
                    Status:       status.Equals("Disabled", StringComparison.OrdinalIgnoreCase)
                                  ? StartupItemStatus.Disabled : StartupItemStatus.Enabled,
                    Publisher:    string.Empty,
                    RegistryPath: null,
                    DelayMinutes: 0));
            }
        }
        catch (Exception ex) { log.LogDebug(ex, "Error reading scheduled tasks"); }
    }

    private void ReadServices(List<StartupItem> items)
    {
        try
        {
            foreach (ServiceController svc in ServiceController.GetServices())
            {
                try
                {
                    // Only include auto-start non-Microsoft services
                    using var key = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Services\{svc.ServiceName}");
                    if (key == null) continue;

                    int startType = (int)(key.GetValue("Start") ?? ServiceStartManual);
                    if (startType > ServiceStartAuto) continue; // 0=Boot,1=System,2=Auto — skip Manual(3)/Disabled(4)

                    string? imagePath = key.GetValue("ImagePath") as string ?? string.Empty;
                    if (imagePath.Contains(@"System32\", StringComparison.OrdinalIgnoreCase) &&
                        !imagePath.Contains("svchost", StringComparison.OrdinalIgnoreCase))
                        continue; // skip most built-in Windows services

                    bool delayed = (int)(key.GetValue("DelayedAutostart") ?? 0) == 1;

                    items.Add(new StartupItem(
                        Name:         svc.DisplayName,
                        Command:      imagePath,
                        Source:       StartupItemSource.WindowsService,
                        Status:       delayed
                                      ? StartupItemStatus.DelayedAutoStart
                                      : StartupItemStatus.Enabled,
                        Publisher:    GetPublisher(imagePath),
                        RegistryPath: $@"HKLM\SYSTEM\CurrentControlSet\Services\{svc.ServiceName}",
                        DelayMinutes: 0));
                }
                catch (Exception ex) { log.LogDebug(ex, "Error reading service {Svc}", svc.ServiceName); }
            }
        }
        catch (Exception ex) { log.LogDebug(ex, "Error enumerating services"); }
    }

    // ── Enable / Disable / Delay ──────────────────────────────────────────────

    public bool SetStatus(StartupItem item, StartupItemStatus newStatus, int delayMinutes)
    {
        try
        {
            return item.Source switch
            {
                StartupItemSource.RegistryRunHklm or
                StartupItemSource.RegistryRunHkcu or
                StartupItemSource.RegistryRunOncHklm or
                StartupItemSource.RegistryRunOnceHkcu => SetRegistryStatus(item, newStatus, delayMinutes),

                StartupItemSource.ScheduledTask       => SetTaskStatus(item, newStatus, delayMinutes),
                StartupItemSource.WindowsService      => SetServiceStatus(item, newStatus, delayMinutes),
                _                                     => false
            };
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SetStatus failed for {Name}", item.Name);
            return false;
        }
    }

    private static bool SetRegistryStatus(StartupItem item, StartupItemStatus newStatus, int delay)
    {
        if (item.RegistryPath == null) return false;

        // Parse hive + path + valueName from stored RegistryPath
        int lastSlash = item.RegistryPath.LastIndexOf('\\');
        if (lastSlash < 0) return false;

        string keyPath    = item.RegistryPath[..lastSlash];
        string valueName  = item.RegistryPath[(lastSlash + 1)..];
        bool   isHklm     = keyPath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase);
        RegistryKey hive  = isHklm ? Registry.LocalMachine : Registry.CurrentUser;
        string subPath    = keyPath[(keyPath.IndexOf('\\') + 1)..];

        // Standard disable: rename key path to Run (disabled)
        if (newStatus == StartupItemStatus.Disabled)
        {
            string disabledPath = subPath.Replace("Run", "Run (disabled)");
            using var src  = hive.OpenSubKey(subPath);
            using var dest = hive.CreateSubKey(disabledPath, writable: true);
            if (src == null || dest == null) return false;
            dest.SetValue(valueName, item.Command);
            src.DeleteValue(valueName, throwOnMissingValue: false);
        }
        else if (newStatus == StartupItemStatus.Enabled)
        {
            // Move back from Run (disabled) to Run
            string disabledPath = subPath.Replace("Run", "Run (disabled)");
            string enabledPath  = subPath.Replace(" (disabled)", string.Empty);
            using var src  = hive.OpenSubKey(disabledPath);
            using var dest = hive.CreateSubKey(enabledPath, writable: true);
            if (src == null || dest == null) return false;
            dest.SetValue(valueName, item.Command);
            src.DeleteValue(valueName, throwOnMissingValue: false);
        }
        else if (newStatus == StartupItemStatus.DelayedAutoStart && delay > 0)
        {
            // Create a scheduled task as wrapper with delay, remove from Run key
            RegisterDelayedTask(item.Name, item.Command, delay);
            using var key = hive.OpenSubKey(subPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }

        return true;
    }

    private static bool SetTaskStatus(StartupItem item, StartupItemStatus newStatus, int delay)
    {
        string taskName = item.Command; // stored full task path
        string arg = newStatus switch
        {
            StartupItemStatus.Disabled => $"/change /tn \"{taskName}\" /disable",
            StartupItemStatus.Enabled  => $"/change /tn \"{taskName}\" /enable",
            _                          => string.Empty
        };
        if (string.IsNullOrEmpty(arg)) return false;
        using var p = Process.Start(new ProcessStartInfo("schtasks.exe", arg)
            { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit(CliTimeoutMs);
        return p.ExitCode == 0;
    }

    private static readonly HashSet<string> CriticalServiceNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Core OS
            "wininit", "lsass", "services", "smss", "csrss",
            // Security
            "windefend", "mpssvc", "bfe", "wdnissvc", "sense",
            "seclogon", "lsm",
            // Networking / connectivity
            "dhcp", "dnscache", "nsi", "tcpip", "netbt", "tdx",
            "afd", "netman", "nlasvc",
            // Storage / file system
            "disk", "volmgr", "partmgr", "ntfs", "fvevol",
            "storflt", "classpnp",
            // Power / hardware
            "power", "hidserv", "acpi",
            // Windows Update / servicing
            "wuauserv", "trustedinstaller", "cryptsvc",
            // RPC / COM
            "rpcss", "dcomlaunch", "rpceptmapper",
            // Event logging / diagnostics
            "eventlog", "diagtrack",
            // Audio (covered separately in AudioEndpointEngine but guard here too)
            "audiosrv", "audioendpointbuilder",
        };

    private static bool SetServiceStatus(StartupItem item, StartupItemStatus newStatus, int delay)
    {
        if (item.RegistryPath == null) return false;
        string svcName = item.RegistryPath.Split('\\').Last();

        if (newStatus == StartupItemStatus.Disabled &&
            CriticalServiceNames.Contains(svcName))
        {
            return false; // Refuse to disable critical services
        }

        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{svcName}", writable: true);
        if (key == null) return false;

        if (newStatus == StartupItemStatus.Disabled)
        {
            key.SetValue("Start", ServiceStartDisabled);
        }
        else if (newStatus == StartupItemStatus.Enabled)
        {
            key.SetValue("Start", ServiceStartAuto);              // Auto
            key.SetValue("DelayedAutostart", 0);
        }
        else if (newStatus == StartupItemStatus.DelayedAutoStart)
        {
            key.SetValue("Start", ServiceStartAuto);              // Auto (required for delayed)
            key.SetValue("DelayedAutostart", 1);   // Delayed ~2 min after boot
        }
        return true;
    }

    private static void RegisterDelayedTask(string name, string command, int delayMinutes)
    {
        string safeName = name.Replace(" ", "_");
        var (exe, args) = SplitCommandLine(command);
        string escapedExe = SecurityElement.Escape(exe) ?? string.Empty;
        string escapedArgs = SecurityElement.Escape(args) ?? string.Empty;
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <LogonTrigger>
                  <Delay>PT{delayMinutes}M</Delay>
                </LogonTrigger>
              </Triggers>
              <Actions>
                <Exec>
                  <Command>{escapedExe}</Command>
                  <Arguments>{escapedArgs}</Arguments>
                </Exec>
              </Actions>
              <Principals>
                <Principal>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
            </Task>
            """;

        string tmpXml = Path.Combine(Path.GetTempPath(), $"dclean_task_{safeName}.xml");
        File.WriteAllText(tmpXml, xml, System.Text.Encoding.Unicode);
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe",
                $"/create /tn \"DClean_Delayed_{safeName}\" /xml \"{tmpXml}\" /f")
                { UseShellExecute = false, CreateNoWindow = true })!;
            p.WaitForExit(TaskCreateTimeout);
        }
        finally { File.Delete(tmpXml); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string GetPublisher(string cmdLine)
    {
        try
        {
            string exe = cmdLine.Trim().TrimStart('"');
            int q = exe.IndexOf('"');
            int s = exe.IndexOf(' ');
            if (q > 0) exe = exe[..q];
            else if (s > 0) exe = exe[..s];
            if (File.Exists(exe))
                return FileVersionInfo.GetVersionInfo(exe).CompanyName ?? string.Empty;
        }
        catch (Exception ex) { log.LogDebug(ex, "Could not get publisher from {Cmd}", cmdLine); }
        return string.Empty;
    }

    private static (string Exe, string Args) SplitCommandLine(string cmdLine)
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
            ? (cmdLine[..space], cmdLine[(space + 1)..].Trim())
            : (cmdLine, string.Empty);
    }

    private static string[] SplitCsv(string line)
    {
        var result = new List<string>();
        bool inQuote = false;
        var current  = new System.Text.StringBuilder();
        foreach (char c in line)
        {
            if (c == '"') { inQuote = !inQuote; }
            else if (c == ',' && !inQuote) { result.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        result.Add(current.ToString());
        return result.ToArray();
    }
}
