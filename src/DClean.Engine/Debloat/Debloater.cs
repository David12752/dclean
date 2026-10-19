using DClean.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace DClean.Engine.Debloat;

[SupportedOSPlatform("windows")]
public sealed class Debloater(ILogger<Debloater> log)
{
    // ── Appx package list ──────────────────────────────────────────────────────

    public IReadOnlyList<AppxPackageEntry> GetInstalledAppxPackages()
    {
        var result = new List<AppxPackageEntry>();
        try
        {
            string output = RunCapture("powershell",
                "-NoProfile -Command \"Get-AppxPackage | " +
                "Select-Object Name,Version,PackageFullName,IsFramework | " +
                "ConvertTo-Csv -NoTypeInformation\"");

            bool first = true;
            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (first) { first = false; continue; } // skip header
                string[] cols = SplitCsv(line);
                if (cols.Length < 4) continue;

                string name     = cols[0].Trim('"');
                string version  = cols[1].Trim('"');
                string fullName = cols[2].Trim('"');
                bool   isFramework = string.Equals(cols[3].Trim('"'), "True",
                    StringComparison.OrdinalIgnoreCase);

                if (isFramework) continue; // skip frameworks
                if (string.IsNullOrWhiteSpace(name)) continue;

                bool isSafe = IsSafeToRemove(name);
                result.Add(new AppxPackageEntry(name, version, fullName, isSafe));
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to enumerate Appx packages");
        }

        return result.OrderByDescending(p => p.IsSafeToRemove).ThenBy(p => p.Name).ToList();
    }

    public bool RemoveAppxPackage(string packageFullName)
    {
        try
        {
            string output = RunCapture("powershell",
                $"-NoProfile -Command \"Remove-AppxPackage -Package '{packageFullName}'\"");
            log.LogInformation("Removed Appx: {Pkg}", packageFullName);
            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to remove Appx: {Pkg}", packageFullName);
            return false;
        }
    }

    // ── Telemetry registry tweaks ──────────────────────────────────────────────

    public IReadOnlyList<TelemetryTweak> GetTelemetryTweaks()
    {
        var tweaks = BuildTweakList();
        foreach (var t in tweaks)
        {
            t.IsCurrentlyEnabled = IsTweakEnabled(t);
        }
        return tweaks;
    }

    public bool ApplyTweak(TelemetryTweak tweak, bool enable)
    {
        try
        {
            using var key = OpenOrCreate(tweak.RegistryPath, writable: true);
            if (key == null) return false;

            if (enable == tweak.DisablesFeature)
            {
                // Applying "disable" → set the value that disables the feature
                key.SetValue(tweak.ValueName, tweak.DisableValue,
                    tweak.ValueName.EndsWith("String") ? RegistryValueKind.String : RegistryValueKind.DWord);
            }
            else
            {
                // Restoring → set the enable value or delete the key
                if (tweak.EnableValue == null)
                    key.DeleteValue(tweak.ValueName, throwOnMissingValue: false);
                else
                    key.SetValue(tweak.ValueName, tweak.EnableValue,
                        tweak.ValueName.EndsWith("String") ? RegistryValueKind.String : RegistryValueKind.DWord);
            }

            log.LogInformation("Applied tweak {Name} enabled={E}", tweak.Name, enable);
            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to apply tweak {Name}", tweak.Name);
            return false;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static bool IsSafeToRemove(string name)
    {
        foreach (string safe in SafeToRemovePatterns)
            if (name.Contains(safe, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsTweakEnabled(TelemetryTweak t)
    {
        try
        {
            using var key = OpenOrCreate(t.RegistryPath, writable: false);
            if (key == null)
            {
                // Key doesn't exist → feature is at default (usually enabled)
                return !t.DisablesFeature;
            }
            var val = key.GetValue(t.ValueName);
            if (val == null) return !t.DisablesFeature; // default

            // Check if current value matches the "disable" value
            bool matchesDisable = val is int i
                ? i == Convert.ToInt32(t.DisableValue)
                : string.Equals(val.ToString(), t.DisableValue?.ToString(),
                    StringComparison.OrdinalIgnoreCase);

            return t.DisablesFeature ? !matchesDisable : matchesDisable;
        }
        catch { return true; }
    }

    private static RegistryKey? OpenOrCreate(string path, bool writable)
    {
        if (path.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase))
            return Registry.LocalMachine.OpenSubKey(path[5..], writable)
                ?? (writable ? Registry.LocalMachine.CreateSubKey(path[5..]) : null);
        if (path.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase))
            return Registry.CurrentUser.OpenSubKey(path[5..], writable)
                ?? (writable ? Registry.CurrentUser.CreateSubKey(path[5..]) : null);
        return null;
    }

    private static string RunCapture(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        })!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30_000);
        return output;
    }

    private static string[] SplitCsv(string line)
    {
        var result  = new List<string>();
        bool inQ    = false;
        var cur     = new System.Text.StringBuilder();
        foreach (char c in line)
        {
            if (c == '"') inQ = !inQ;
            else if (c == ',' && !inQ) { result.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        result.Add(cur.ToString());
        return result.ToArray();
    }

    // ── Curated lists ──────────────────────────────────────────────────────────

    private static readonly string[] SafeToRemovePatterns =
    [
        "3DViewer", "BingNews", "BingWeather", "BingSports",
        "BingFinance", "BingTravel", "BingFood",
        "Getstarted", "GetHelp", "MicrosoftSolitaire",
        "MixedReality", "Office.OneNote", "OneConnect",
        "People", "Print3D", "SkypeApp", "Todos",
        "Wallet", "WebExperience", "WindowsMaps",
        "Xbox", "ZuneMusic", "ZuneVideo",
        "Netflix", "Spotify", "Disney", "Facebook", "Twitter",
        "CandyCrush", "Bubble", "FarmHeroes", "March", "Royal",
    ];

    private static List<TelemetryTweak> BuildTweakList() =>
    [
        new TelemetryTweak(
            Name:            "Telemetry — רמת איסוף נתונים",
            Description:     "מגביל את שליחת נתוני שימוש ל-Microsoft לרמה 'Security' (0) בלבד",
            Category:        "פרטיות",
            RegistryPath:    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
            ValueName:       "AllowTelemetry",
            DisableValue:    0,
            EnableValue:     1,
            DisablesFeature: true,
            IsCurrentlyEnabled: true),

        new TelemetryTweak(
            Name:            "Activity History",
            Description:     "מונע שמירת היסטוריית פעולות ב-Timeline של Windows",
            Category:        "פרטיות",
            RegistryPath:    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\System",
            ValueName:       "PublishUserActivities",
            DisableValue:    0,
            EnableValue:     1,
            DisablesFeature: true,
            IsCurrentlyEnabled: true),

        new TelemetryTweak(
            Name:            "Advertising ID",
            Description:     "כיבוי זיהוי פרסומי ייחודי לאפליקציות",
            Category:        "פרטיות",
            RegistryPath:    @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
            ValueName:       "Enabled",
            DisableValue:    0,
            EnableValue:     1,
            DisablesFeature: true,
            IsCurrentlyEnabled: true),

        new TelemetryTweak(
            Name:            "Cortana Web Search",
            Description:     "מנטרל חיפוש רשת דרך Cortana / שורת החיפוש",
            Category:        "פרטיות",
            RegistryPath:    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search",
            ValueName:       "DisableWebSearch",
            DisableValue:    1,
            EnableValue:     0,
            DisablesFeature: true,
            IsCurrentlyEnabled: true),

        new TelemetryTweak(
            Name:            "Windows Error Reporting",
            Description:     "מנטרל שליחה אוטומטית של דוחות שגיאה ל-Microsoft",
            Category:        "פרטיות",
            RegistryPath:    @"HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting",
            ValueName:       "Disabled",
            DisableValue:    1,
            EnableValue:     0,
            DisablesFeature: true,
            IsCurrentlyEnabled: true),

        new TelemetryTweak(
            Name:            "Location Tracking",
            Description:     "מנטרל מעקב מיקום לרמת מערכת",
            Category:        "פרטיות",
            RegistryPath:    @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Sensor\Overrides\{BFA794E4-F964-4FDB-90F6-51056BFE4B44}",
            ValueName:       "SensorPermissionState",
            DisableValue:    0,
            EnableValue:     1,
            DisablesFeature: true,
            IsCurrentlyEnabled: true),

        new TelemetryTweak(
            Name:            "Delivery Optimization",
            Description:     "מנטרל שיתוף עדכוני Windows לעמיתים ברשת (P2P)",
            Category:        "ביצועים",
            RegistryPath:    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization",
            ValueName:       "DODownloadMode",
            DisableValue:    0,
            EnableValue:     null,
            DisablesFeature: true,
            IsCurrentlyEnabled: true),

        new TelemetryTweak(
            Name:            "Superfetch / SysMain",
            Description:     "מנטרל את שירות Prefetch — מפחית I/O על SSD",
            Category:        "ביצועים",
            RegistryPath:    @"HKLM\SYSTEM\CurrentControlSet\Services\SysMain",
            ValueName:       "Start",
            DisableValue:    4,
            EnableValue:     2,
            DisablesFeature: true,
            IsCurrentlyEnabled: true),

        new TelemetryTweak(
            Name:            "Fast Startup",
            Description:     "מנטרל Hybrid Boot — עלול לגרום בעיות עם dual-boot ו-BitLocker",
            Category:        "ביצועים",
            RegistryPath:    @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power",
            ValueName:       "HiberbootEnabled",
            DisableValue:    0,
            EnableValue:     1,
            DisablesFeature: true,
            IsCurrentlyEnabled: true),
    ];
}
