using DClean.Contracts;
using DClean.Engine.Interop;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DClean.Engine.Memory;

public sealed class RamMonitor : IDisposable
{
    private readonly PerformanceCounter? _commitCounter;

    public RamMonitor()
    {
        try
        {
            _commitCounter = new PerformanceCounter("Memory", "% Committed Bytes In Use",
                readOnly: true);
        }
        catch
        {
            _commitCounter = null;
        }
    }

    public RamSnapshot GetSnapshot()
    {
        var status = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };
        NativeMethods.GlobalMemoryStatusEx(ref status);

        float commitPercent = 0f;
        try { commitPercent = _commitCounter?.NextValue() ?? 0f; }
        catch { }

        return new RamSnapshot(
            TotalBytes:          (long)status.ullTotalPhys,
            AvailableBytes:      (long)status.ullAvailPhys,
            UsedPercent:         status.dwMemoryLoad,
            CommitChargePercent: commitPercent);
    }

    public void Dispose() => _commitCounter?.Dispose();
}
