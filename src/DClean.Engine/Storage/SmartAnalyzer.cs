using DClean.Contracts;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DClean.Engine.Storage;

[SupportedOSPlatform("windows")]
public sealed class SmartAnalyzer(ILogger<SmartAnalyzer> log)
{
    private static readonly Dictionary<byte, (string Name, bool Critical)> KnownAttrs = new()
    {
        [0x01] = ("Raw Read Error Rate",          false),
        [0x05] = ("Reallocated Sectors Count",     true),
        [0x09] = ("Power-On Hours",                false),
        [0x0C] = ("Power Cycle Count",             false),
        [0xAB] = ("Program Fail Count (SSD)",      true),
        [0xAC] = ("Erase Fail Count (SSD)",        true),
        [0xAD] = ("Wear Leveling Count",           false),
        [0xB8] = ("End-to-End Error",              true),
        [0xBB] = ("Uncorrectable Error Count",     true),
        [0xBC] = ("Command Timeout",               false),
        [0xBE] = ("Airflow Temperature",           false),
        [0xC0] = ("Unsafe Shutdown Count",         false),
        [0xC2] = ("Temperature",                   false),
        [0xC5] = ("Current Pending Sectors",       true),
        [0xC6] = ("Offline Uncorrectable",         true),
        [0xC7] = ("Ultra DMA CRC Error",           false),
        [0xE9] = ("NAND Writes (1 GB)",            false),
        [0xF1] = ("Total Host Writes",             false),
        [0xF2] = ("Total Host Reads",              false),
    };

    // ── Public API ────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DriveHealth>> AnalyzeAllDrivesAsync(
        CancellationToken ct = default)
    {
        var results = new List<DriveHealth>();

        // Use wmic as a fallback — no admin needed for basic info, and it works on all Win10+
        var drives = await GetDrivesViaWmicAsync(ct).ConfigureAwait(false);

        foreach (var (model, serial, sizeGb, attrs) in drives)
        {
            int score = CalcHealthScore(attrs);
            long tbwBytes = GetTotalBytesWritten(attrs);
            int tempC     = GetTemperature(attrs);
            long powerOnH = GetPowerOnHours(attrs);

            results.Add(new DriveHealth(
                Model:           model,
                SerialNumber:    serial,
                CapacityBytes:   (long)sizeGb * 1_073_741_824L,
                HealthScore:     score,
                TemperatureCelsius: tempC,
                PowerOnHours:    powerOnH,
                TotalBytesWritten: tbwBytes,
                TrimEnabled:     await CheckTrimAsync(ct),
                Attributes:      attrs));
        }

        return results;
    }

    public async Task<bool> EnableTrimAsync(CancellationToken ct = default)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("fsutil.exe",
                "behavior set DisableDeleteNotify 0")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow  = true
            })!;
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            log.LogError("Enable TRIM failed: {Msg}", ex.Message);
            return false;
        }
    }

    // ── Internal ──────────────────────────────────────────────────────

    private async Task<IReadOnlyList<(string Model, string Serial, long SizeGb, IReadOnlyList<SmartAttributeEntry> Attrs)>>
        GetDrivesViaWmicAsync(CancellationToken ct)
    {
        var results = new List<(string, string, long, IReadOnlyList<SmartAttributeEntry>)>();
        try
        {
            // Get drive list via PowerShell Get-Disk (simpler than raw WMI from Engine)
            string output = await RunCaptureAsync(
                "powershell.exe",
                "-NoProfile -NonInteractive -Command \"Get-PhysicalDisk | Select-Object -Property FriendlyName,SerialNumber,Size | ConvertTo-Json -Compress\"",
                ct).ConfigureAwait(false);

            // Parse
            var drives = ParseDriveListJson(output);
            foreach (var (model, serial, sizeBytes) in drives)
            {
                var attrs = await GetSmartAttrsViaWmiAsync(model, ct).ConfigureAwait(false);
                results.Add((model, serial, sizeBytes / 1_073_741_824L, attrs));
            }
        }
        catch (Exception ex)
        {
            log.LogWarning("Drive enumeration failed: {Msg}", ex.Message);
            // Fallback: return basic info from DriveInfo
            foreach (var di in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
                results.Add((di.Name, "", di.TotalSize / 1_073_741_824L, []));
        }
        return results;
    }

    private async Task<IReadOnlyList<SmartAttributeEntry>> GetSmartAttrsViaWmiAsync(
        string model, CancellationToken ct)
    {
        var attrs = new List<SmartAttributeEntry>();
        try
        {
            // PowerShell + CIM to read MSStorageDriver_ATAPISmartData
            string ps = """
                $d = Get-WmiObject -Namespace root\wmi -Class MSStorageDriver_ATAPISmartData -EA SilentlyContinue
                if ($d) { $d | Select-Object -ExpandProperty VendorSpecific | ConvertTo-Json -Compress }
                """;
            string output = await RunCaptureAsync("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"{ps.Replace("\"", "\\\"")}\"", ct).ConfigureAwait(false);

            attrs = ParseSmartBuffer(output);
        }
        catch (Exception ex) { log.LogDebug(ex, "No SMART data available"); }
        return attrs;
    }

    private static List<SmartAttributeEntry> ParseSmartBuffer(string output)
    {
        var attrs = new List<SmartAttributeEntry>();
        // Parse JSON array of bytes [2,0,5,0,200,200,0,0,0,0,0,0, ...] (12-byte structs)
        int[]? nums = null;
        try
        {
            nums = JsonSerializer.Deserialize<int[]>(output.Trim());
        }
        catch { /* Fallback: not valid JSON */ }

        if (nums is null || nums.Length < 14) return attrs;

        for (int i = 2; i + 11 < nums.Length; i += 12)
        {
            byte id  = (byte)nums[i];
            if (id == 0) continue;
            byte val = (byte)nums[i + 3];
            byte wst = (byte)nums[i + 4];
            byte thr = (byte)nums[i + 5];
            long raw = 0;
            for (int b = 0; b < 6; b++)
                raw |= (long)(byte)nums[i + 6 + b] << (b * 8);

            KnownAttrs.TryGetValue(id, out var def);
            attrs.Add(new SmartAttributeEntry(
                Id:         id,
                Name:       def.Name ?? $"Attr 0x{id:X2}",
                Value:      val,
                Worst:      wst,
                Threshold:  thr,
                RawValue:   raw,
                IsCritical: def.Critical,
                IsHealthy:  val >= thr));
        }
        return attrs;
    }

    private static IReadOnlyList<(string Model, string Serial, long SizeBytes)> ParseDriveListJson(string json)
    {
        var list = new List<(string, string, long)>();
        // Simple regex parse to avoid JSON dependency
        var names   = Regex.Matches(json, @"""FriendlyName""\s*:\s*""([^""]+)""");
        var serials = Regex.Matches(json, @"""SerialNumber""\s*:\s*""([^""]*)""");
        var sizes   = Regex.Matches(json, @"""Size""\s*:\s*(\d+)");
        int count   = Math.Min(names.Count, Math.Min(serials.Count, sizes.Count));
        for (int i = 0; i < count; i++)
            list.Add((names[i].Groups[1].Value, serials[i].Groups[1].Value,
                      long.TryParse(sizes[i].Groups[1].Value, out long s) ? s : 0));
        return list;
    }

    private static int CalcHealthScore(IReadOnlyList<SmartAttributeEntry> attrs)
    {
        int score = 100;
        foreach (var a in attrs)
        {
            if (!a.IsCritical) continue;
            if (!a.IsHealthy)  score -= 30;
            else if (a.RawValue > 0) score -= 10;
        }
        return Math.Max(0, score);
    }

    private static long GetTotalBytesWritten(IReadOnlyList<SmartAttributeEntry> attrs)
    {
        var f1 = attrs.FirstOrDefault(a => a.Id == 0xF1);
        if (f1 != null) return f1.RawValue * 1_073_741_824L;
        var e9 = attrs.FirstOrDefault(a => a.Id == 0xE9);
        if (e9 != null) return e9.RawValue * 1_073_741_824L;
        return -1;
    }

    private static int GetTemperature(IReadOnlyList<SmartAttributeEntry> attrs)
    {
        var c2 = attrs.FirstOrDefault(a => a.Id == 0xC2);
        return c2 != null ? (int)(c2.RawValue & 0xFF) : -1;
    }

    private static long GetPowerOnHours(IReadOnlyList<SmartAttributeEntry> attrs)
    {
        var a9 = attrs.FirstOrDefault(a => a.Id == 0x09);
        return a9?.RawValue ?? -1;
    }

    private async Task<bool> CheckTrimAsync(CancellationToken ct)
    {
        try
        {
            string out1 = await RunCaptureAsync("fsutil.exe",
                "behavior query DisableDeleteNotify", ct).ConfigureAwait(false);
            return out1.Contains("= 0");
        }
        catch (Exception ex) { log.LogDebug(ex, "Could not check TRIM status"); return false; }
    }

    private static async Task<string> RunCaptureAsync(
        string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };
        using var proc = Process.Start(psi)!;
        string output  = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return output;
    }
}
