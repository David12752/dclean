using DClean.Contracts;
using DClean.Engine.Interop;
using DClean.Engine.Logging;
using DClean.Engine.Safety;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace DClean.Engine.Memory;

public sealed class WorkingSetTrimmer
{
    private readonly DryRunGuard _guard;
    private readonly ILogger<WorkingSetTrimmer> _log;

    public WorkingSetTrimmer(DryRunGuard guard, ILogger<WorkingSetTrimmer> log)
    {
        _guard = guard;
        _log   = log;
    }

    public TrimResult Trim(ProcessInfo proc)
    {
        if (proc.IsCritical)
        {
            DCleanEventSource.Log.ProcessTerminationBlocked(proc.Pid, proc.Name, "Critical process");
            return new TrimResult(proc.Pid, proc.Name, false, "Critical process — blocked");
        }

        if (!_guard.IsExecuteMode)
        {
            _log.LogInformation("[DRY-RUN] Would trim working set of {Name} (PID {Pid})",
                proc.Name, proc.Pid);
            return new TrimResult(proc.Pid, proc.Name, true, "DryRun");
        }

        var hProcess = IntPtr.Zero;
        try
        {
            hProcess = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_SET_QUOTA | NativeMethods.PROCESS_QUERY_INFORMATION,
                false, (uint)proc.Pid);

            if (hProcess == IntPtr.Zero)
                return new TrimResult(proc.Pid, proc.Name, false,
                    $"OpenProcess failed: {Marshal.GetLastWin32Error()}");

            bool ok = NativeMethods.EmptyWorkingSet(hProcess);
            if (ok)
            {
                DCleanEventSource.Log.WorkingSetTrimmed(proc.Pid, proc.Name);
                _log.LogInformation("Trimmed working set of {Name} (PID {Pid})", proc.Name, proc.Pid);
                return new TrimResult(proc.Pid, proc.Name, true, string.Empty);
            }

            return new TrimResult(proc.Pid, proc.Name, false,
                $"EmptyWorkingSet failed: {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "WorkingSet trim failed for {Name} (PID {Pid})", proc.Name, proc.Pid);
            return new TrimResult(proc.Pid, proc.Name, false, ex.Message);
        }
        finally
        {
            if (hProcess != IntPtr.Zero) NativeMethods.CloseHandle(hProcess);
        }
    }
}
