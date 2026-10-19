using DClean.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;

namespace DClean.Engine.Drivers;

[SupportedOSPlatform("windows")]
public sealed class DriverAuditor(ILogger<DriverAuditor> log)
{
    public IReadOnlyList<DriverInfo> EnumerateDrivers()
    {
        var results = new List<DriverInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPSignedDriver WHERE DriverName IS NOT NULL");
            foreach (ManagementObject obj in searcher.Get())
            {
                try
                {
                    string name    = obj["DeviceName"]   as string ?? "";
                    string version = obj["DriverVersion"] as string ?? "";
                    string date    = obj["DriverDate"]    as string ?? "";
                    string provider= obj["DriverProviderName"] as string ?? "";
                    bool   signed  = obj["IsSigned"] is bool b && b;
                    string devClass= obj["DeviceClass"]  as string ?? "";
                    string infName = obj["InfName"]      as string ?? "";

                    if (string.IsNullOrWhiteSpace(name)) continue;

                    results.Add(new DriverInfo(
                        DeviceName:    name,
                        Version:       version,
                        DriverDate:    ParseWmiDate(date),
                        Provider:      provider,
                        IsSigned:      signed,
                        DeviceClass:   devClass,
                        InfName:       infName,
                        Status:        DriverStatus.Unknown));
                }
                catch (Exception ex) { log.LogDebug(ex, "Skipped WMI driver entry"); }
            }
        }
        catch (Exception ex)
        {
            log.LogWarning("WMI driver enumeration failed: {Msg}", ex.Message);
        }

        return results.OrderBy(d => d.DeviceClass).ThenBy(d => d.DeviceName).ToList();
    }

    public async Task<string> ExportDriversAsync(CancellationToken ct = default)
    {
        string dest = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "DClean", "DriverBackup",
            $"drivers_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(dest);

        try
        {
            var psi = new ProcessStartInfo("dism.exe",
                $"/Online /Export-Driver /Destination:\"{dest}\"")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            };
            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            log.LogInformation("Driver export to {Dest}, exit={Code}", dest, proc.ExitCode);
            return proc.ExitCode == 0 ? dest : string.Empty;
        }
        catch (Exception ex)
        {
            log.LogError("Driver export failed: {Msg}", ex.Message);
            return string.Empty;
        }
    }

    private static DateTime? ParseWmiDate(string wmiDate)
    {
        if (string.IsNullOrWhiteSpace(wmiDate) || wmiDate.Length < 8) return null;
        if (int.TryParse(wmiDate[..4], out int y) &&
            int.TryParse(wmiDate[4..6], out int m) &&
            int.TryParse(wmiDate[6..8], out int d))
            return new DateTime(y, Math.Clamp(m, 1, 12), Math.Clamp(d, 1, 28));
        return null;
    }
}
