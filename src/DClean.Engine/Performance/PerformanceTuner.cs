using DClean.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DClean.Engine.Performance;

[SupportedOSPlatform("windows")]
public sealed class PerformanceTuner(ILogger<PerformanceTuner> log)
{
    // ── Constants ────────────────────────────────────────────────────────────
    private const int CliTimeoutMs         = 5_000;
    private const uint AllCoresUnparked    = 100u;
    private const int  CoreParkThreshold   = 100;

    // ── P/Invoke — powrprof.dll ───────────────────────────────────────────────

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerSetActiveScheme(IntPtr RootPowerKey, ref Guid SchemeGuid);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerGetActiveScheme(IntPtr RootPowerKey, out IntPtr ActivePolicyGuid);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerWriteACValueIndex(IntPtr RootPowerKey,
        ref Guid SchemeGuid, ref Guid SubGroupGuid, ref Guid PowerSettingGuid, uint AcValueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerWriteDCValueIndex(IntPtr RootPowerKey,
        ref Guid SchemeGuid, ref Guid SubGroupGuid, ref Guid PowerSettingGuid, uint DcValueIndex);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    // ── Well-known GUIDs ──────────────────────────────────────────────────────

    private static readonly Guid GUID_ULTIMATE     = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    private static readonly Guid GUID_HIGH_PERF    = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid GUID_BALANCED     = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    // USB sub-group + selective suspend setting
    private static readonly Guid GUID_USB_SUBGROUP  = new("2a737441-1930-4402-8d77-b2bebba308a3");
    private static readonly Guid GUID_USB_SS         = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");

    // Processor power management sub-group
    private static readonly Guid GUID_PROC_SUBGROUP  = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid GUID_PARK_MIN_CORES = new("0cc5b647-c1df-4637-891a-dec35c318583");
    private static readonly Guid GUID_PARK_MAX_CORES = new("ea062031-0e34-4ff1-9b6d-eb1059334028");

    // ── Public API ────────────────────────────────────────────────────────────

    public PerformanceProfile ReadCurrentProfile()
    {
        string activeName   = GetActivePlanName();
        bool   usbSsEnabled = ReadUsbSelectiveSuspend();
        bool   parkEnabled  = ReadCoreParkingEnabled();

        return new PerformanceProfile(activeName, usbSsEnabled, parkEnabled);
    }

    public bool ApplyUltimatePlan()
    {
        Guid ult  = GUID_ULTIMATE;
        Guid high = GUID_HIGH_PERF;
        uint result = PowerSetActiveScheme(IntPtr.Zero, ref ult);
        if (result == 0) { log.LogInformation("Activated Ultimate Performance plan"); return true; }

        log.LogInformation("Ultimate Performance not found, duplicating from High Performance...");
        RunCli("powercfg", $"/duplicatescheme {GUID_HIGH_PERF} {GUID_ULTIMATE}");
        ult    = GUID_ULTIMATE;
        result = PowerSetActiveScheme(IntPtr.Zero, ref ult);
        if (result == 0) return true;

        result = PowerSetActiveScheme(IntPtr.Zero, ref high);
        log.LogWarning("Could not activate Ultimate, fell back to High Performance (result={R})", result);
        return result == 0;
    }

    public bool ApplyBalancedPlan()
    {
        Guid bal  = GUID_BALANCED;
        uint result = PowerSetActiveScheme(IntPtr.Zero, ref bal);
        return result == 0;
    }

    public bool SetUsbSelectiveSuspend(bool enabled)
    {
        uint value    = enabled ? 1u : 0u;
        Guid scheme   = GetActiveSchemeGuid();
        Guid usbSub   = GUID_USB_SUBGROUP;
        Guid usbSs    = GUID_USB_SS;
        uint acResult = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref usbSub, ref usbSs, value);
        uint dcResult = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref usbSub, ref usbSs, value);

        RunCli("powercfg", $"/setactive {scheme}");
        log.LogInformation("USB Selective Suspend set to {V} (AC={A} DC={D})", enabled, acResult, dcResult);
        return acResult == 0 && dcResult == 0;
    }

    public bool SetCoreParkingDisabled(bool disableParking)
    {
        uint minVal   = disableParking ? AllCoresUnparked : 0u;
        uint maxVal   = AllCoresUnparked;
        Guid scheme   = GetActiveSchemeGuid();
        Guid procSub  = GUID_PROC_SUBGROUP;
        Guid parkMin  = GUID_PARK_MIN_CORES;
        Guid parkMax  = GUID_PARK_MAX_CORES;

        uint r1 = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref procSub, ref parkMin, minVal);
        uint r2 = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref procSub, ref parkMax, maxVal);
        uint r3 = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref procSub, ref parkMin, minVal);
        uint r4 = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref procSub, ref parkMax, maxVal);

        RunCli("powercfg", $"/setactive {scheme}");
        log.LogInformation("Core parking disabled={V} (r={R})", disableParking, r1 | r2 | r3 | r4);
        return (r1 | r2 | r3 | r4) == 0;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Guid GetActiveSchemeGuid()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptr) == 0 && ptr != IntPtr.Zero)
        {
            var guid = Marshal.PtrToStructure<Guid>(ptr);
            LocalFree(ptr);
            return guid;
        }
        return GUID_BALANCED;
    }

    private string GetActivePlanName()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("powercfg", "/getactivescheme")
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
            })!;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(CliTimeoutMs);
            // "Power Scheme GUID: xxxxxxxx  (High performance)"
            int p1 = output.IndexOf('(');
            int p2 = output.IndexOf(')');
            return p1 >= 0 && p2 > p1 ? output[(p1 + 1)..p2].Trim() : output.Trim();
        }
        catch (Exception ex) { log.LogDebug(ex, "Could not read active power plan"); return "Unknown"; }
    }

    private bool ReadUsbSelectiveSuspend()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("powercfg",
                "/query SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 " +
                "48e6b7a6-50f5-4782-a5d4-53bb8f07e226")
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
            })!;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(CliTimeoutMs);
            // "Current AC Power Setting Index: 0x00000001" → 1 = enabled
            var match = System.Text.RegularExpressions.Regex.Match(
                output, @"Current AC Power Setting Index: 0x([0-9a-fA-F]+)");
            return match.Success && int.Parse(match.Groups[1].Value,
                System.Globalization.NumberStyles.HexNumber) == 1;
        }
        catch (Exception ex) { log.LogDebug(ex, "Could not read USB selective suspend"); return true; } // assume enabled
    }

    private bool ReadCoreParkingEnabled()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("powercfg",
                "/query SCHEME_CURRENT 54533251-82be-4824-96c1-47b60b740d00 " +
                "0cc5b647-c1df-4637-891a-dec35c318583")
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
            })!;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(CliTimeoutMs);
            var match = System.Text.RegularExpressions.Regex.Match(
                output, @"Current AC Power Setting Index: 0x([0-9a-fA-F]+)");
            if (!match.Success) return true;
            int minCores = int.Parse(match.Groups[1].Value,
                System.Globalization.NumberStyles.HexNumber);
            return minCores < CoreParkThreshold; // <100 means parking can occur
        }
        catch (Exception ex) { log.LogDebug(ex, "Could not read core parking state"); return true; }
    }

    private void RunCli(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
                { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(CliTimeoutMs);
        }
        catch (Exception ex) { log.LogDebug(ex, "CLI failed: {Exe} {Args}", exe, args); }
    }
}
