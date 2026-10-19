using DClean.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DClean.Engine.Uninstaller;

[SupportedOSPlatform("windows")]
public sealed class InstalledAppDiscovery(ILogger<InstalledAppDiscovery> log)
{
    // MSI P/Invoke
    [DllImport("msi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint MsiEnumProductsW(uint iProductIndex, StringBuilder lpProductBuf);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint MsiGetProductInfoW(string szProduct, string szProperty,
        StringBuilder lpValueBuf, ref uint pcchValueBuf);

    private const uint ERROR_SUCCESS      = 0;
    private const uint ERROR_NO_MORE_ITEMS = 259;
    private const int  GUID_BUF_SIZE      = 40;
    private const int  PROP_BUF_SIZE      = 512;

    private static readonly string[] UninstallPaths =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    public IReadOnlyList<InstalledApp> Discover()
    {
        var apps  = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        // Primary: MSI enumeration
        foreach (var app in EnumerateMsi())
            apps.TryAdd(app.DisplayName + "|" + app.Version, app);

        // Fallback: Registry (catches Squirrel, InnoSetup, NSIS, etc.)
        foreach (var app in EnumerateRegistry())
            apps.TryAdd(app.DisplayName + "|" + app.Version, app);

        return [.. apps.Values.OrderBy(a => a.DisplayName)];
    }

    private IEnumerable<InstalledApp> EnumerateMsi()
    {
        var guid = new StringBuilder(GUID_BUF_SIZE);
        uint i = 0;
        while (MsiEnumProductsW(i++, guid) == ERROR_SUCCESS)
        {
            string productCode = guid.ToString();
            string name    = GetMsiProp(productCode, "ProductName");
            if (string.IsNullOrWhiteSpace(name)) continue;

            string publisher = GetMsiProp(productCode, "Publisher");
            string version   = GetMsiProp(productCode, "VersionString");
            string location  = GetMsiProp(productCode, "InstallLocation");
            string sizeStr   = GetMsiProp(productCode, "EstimatedSize");
            long.TryParse(sizeStr, out long sizeKb);

            yield return new InstalledApp(
                Guid:                productCode,
                DisplayName:         name,
                Publisher:           publisher,
                Version:             version,
                InstallLocation:     string.IsNullOrEmpty(location) ? null : location,
                UninstallString:     $"msiexec.exe /x {productCode} /qn /norestart",
                QuietUninstallString: $"msiexec.exe /x {productCode} /qn /norestart",
                EstimatedSizeKb:     sizeKb,
                Type:                InstallerType.MSI,
                DisplayIconPath:     null,
                DiscoverySource:     "MSI product database",
                RegistryKeyPath:     $"MSI:{productCode}");
        }
    }

    private string GetMsiProp(string productCode, string property)
    {
        var buf = new StringBuilder(PROP_BUF_SIZE);
        uint size = (uint)PROP_BUF_SIZE;
        return MsiGetProductInfoW(productCode, property, buf, ref size) == ERROR_SUCCESS
            ? buf.ToString()
            : string.Empty;
    }

    private IEnumerable<InstalledApp> EnumerateRegistry()
    {
        foreach (var (hive, view) in new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser,  RegistryView.Default),
        })
        {
            foreach (string subPath in UninstallPaths)
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var root    = baseKey.OpenSubKey(subPath);
                if (root == null) continue;

                foreach (string keyName in root.GetSubKeyNames())
                {
                    InstalledApp? app = null;
                    try
                    {
                        using var key = root.OpenSubKey(keyName);
                        if (key == null) continue;

                        string? name = key.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        string? uninstall = key.GetValue("UninstallString") as string;
                        if (string.IsNullOrWhiteSpace(uninstall)) continue;

                        // Skip Windows Update entries and system components
                        string? release = key.GetValue("ReleaseType") as string;
                        string? parentKey = key.GetValue("ParentKeyName") as string;
                        if (release is "Security Update" or "Update Rollup" or "Hotfix") continue;
                        if (parentKey == "MicrosoftWindows") continue;
                        if (key.GetValue("SystemComponent") is 1) continue;

                        string?  publisher = key.GetValue("Publisher") as string;
                        string?  version   = key.GetValue("DisplayVersion") as string;
                        string?  location  = key.GetValue("InstallLocation") as string;
                        string?  quietStr  = key.GetValue("QuietUninstallString") as string;
                        string?  iconPath  = key.GetValue("DisplayIcon") as string;
                        long     sizeKb    = Convert.ToInt64(key.GetValue("EstimatedSize") ?? 0L);
                        string?  guid      = keyName.StartsWith('{') ? keyName : null;

                        app = new InstalledApp(
                            Guid:                guid,
                            DisplayName:         name.Trim(),
                            Publisher:           publisher?.Trim() ?? string.Empty,
                            Version:             version?.Trim()   ?? string.Empty,
                            InstallLocation:     string.IsNullOrEmpty(location) ? null : location,
                            UninstallString:     uninstall.Trim(),
                            QuietUninstallString: string.IsNullOrEmpty(quietStr) ? null : quietStr.Trim(),
                            EstimatedSizeKb:     sizeKb,
                            Type:                DetectType(uninstall, location),
                            DisplayIconPath:     string.IsNullOrEmpty(iconPath) ? null : iconPath.Split(',')[0].Trim('"'),
                            DiscoverySource:     $"Registry uninstall entry ({hive}, {view})",
                            RegistryKeyPath:     $@"{hive}\{subPath}\{keyName}");
                    }
                    catch (Exception ex)
                    {
                        log.LogDebug(ex, "Skipping registry key {Key}", keyName);
                    }
                    if (app != null) yield return app;
                }
            }
        }
    }

    private static InstallerType DetectType(string uninstallString, string? location)
    {
        string u = uninstallString.ToLowerInvariant();
        if (u.Contains("msiexec")) return InstallerType.MSI;
        if (u.Contains("update.exe") && u.Contains("uninstall")) return InstallerType.Squirrel;

        if (!string.IsNullOrEmpty(location))
        {
            string unins = Path.Combine(location, "unins000.exe");
            if (File.Exists(unins)) return InstallerType.InnoSetup;

            string uninstallExe = Path.Combine(location, "uninstall.exe");
            if (File.Exists(uninstallExe))
            {
                try
                {
                    var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(uninstallExe);
                    if (vi.CompanyName?.Contains("Nullsoft", StringComparison.OrdinalIgnoreCase) == true)
                        return InstallerType.NSIS;
                }
                catch { }
            }
        }
        return InstallerType.ExeGeneric;
    }
}
