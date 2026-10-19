using DClean.Contracts;
using DClean.Engine.Interop;
using DClean.Engine.Logging;
using DClean.Engine.Safety;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DClean.Engine.Memory;

public sealed class SafeTerminator
{
    private const int GracefulTimeoutMs = 5000;

    private readonly DryRunGuard _guard;
    private readonly ILogger<SafeTerminator> _log;

    public SafeTerminator(DryRunGuard guard, ILogger<SafeTerminator> log)
    {
        _guard = guard;
        _log   = log;
    }

    public async Task<TerminateResult> TerminateAsync(
        ProcessInfo proc,
        bool forceIfNeeded,
        CancellationToken ct = default)
    {
        if (proc.IsCritical || proc.IsProtected)
        {
            DCleanEventSource.Log.ProcessTerminationBlocked(proc.Pid, proc.Name,
                "Critical or protected");
            _log.LogWarning("Termination BLOCKED for {Name} (PID {Pid})", proc.Name, proc.Pid);
            return new TerminateResult(proc.Pid, proc.Name, false, "Blocked: critical or protected");
        }

        if (!_guard.IsExecuteMode)
        {
            _log.LogInformation("[DRY-RUN] Would terminate {Name} (PID {Pid})", proc.Name, proc.Pid);
            return new TerminateResult(proc.Pid, proc.Name, true, "DryRun");
        }

        // Stage 1 — graceful exit via WM_CLOSE
        bool sentClose = SendCloseMessages((uint)proc.Pid);
        if (sentClose)
        {
            bool exited = await WaitForExitAsync(proc.Pid, GracefulTimeoutMs, ct).ConfigureAwait(false);
            if (exited)
            {
                DCleanEventSource.Log.ProcessTerminated(proc.Pid, proc.Name, wasGraceful: true);
                return new TerminateResult(proc.Pid, proc.Name, true, "GracefulExit");
            }
        }

        if (!forceIfNeeded)
        {
            _log.LogWarning("{Name} (PID {Pid}) did not exit gracefully; force-kill not requested.",
                proc.Name, proc.Pid);
            return new TerminateResult(proc.Pid, proc.Name, false, "DidNotExitGracefully_NoForce");
        }

        return ForceKill(proc);
    }

    private static bool SendCloseMessages(uint pid)
    {
        bool anySent = false;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out uint winPid);
            if (winPid == pid)
            {
                NativeMethods.PostMessage(hWnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                anySent = true;
            }
            return true;
        }, IntPtr.Zero);
        return anySent;
    }

    private async Task<bool> WaitForExitAsync(int pid, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (ArgumentException) { return true; }
        catch (Exception ex) { _log.LogDebug(ex, "WaitForExit failed for PID {Pid}", pid); return false; }
    }

    private TerminateResult ForceKill(ProcessInfo proc)
    {
        var hProcess = IntPtr.Zero;
        try
        {
            hProcess = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_TERMINATE | NativeMethods.PROCESS_QUERY_LIMITED_INFO,
                false, (uint)proc.Pid);

            if (hProcess == IntPtr.Zero)
                return new TerminateResult(proc.Pid, proc.Name, false,
                    $"OpenProcess failed: {Marshal.GetLastWin32Error()}");

            // PID recycling guard: verify name matches before killing
            var sb = new StringBuilder(1024);
            uint sz = (uint)sb.Capacity;
            if (NativeMethods.QueryFullProcessImageName(hProcess,
                NativeMethods.PROCESS_NAME_WIN32, sb, ref sz))
            {
                string currentName = Path.GetFileName(sb.ToString());
                if (!string.Equals(currentName, proc.Name, StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogWarning("PID {Pid} recycled! Was '{Old}', now '{New}'. Aborting.",
                        proc.Pid, proc.Name, currentName);
                    return new TerminateResult(proc.Pid, proc.Name, false, "PidRecycled");
                }
            }

            bool ok = NativeMethods.TerminateProcess(hProcess, 1);
            DCleanEventSource.Log.ProcessTerminated(proc.Pid, proc.Name, wasGraceful: false);

            return ok
                ? new TerminateResult(proc.Pid, proc.Name, true, "ForcedTerminate")
                : new TerminateResult(proc.Pid, proc.Name, false,
                    $"TerminateProcess failed: {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ForceKill failed for {Name} (PID {Pid})", proc.Name, proc.Pid);
            return new TerminateResult(proc.Pid, proc.Name, false, ex.Message);
        }
        finally
        {
            if (hProcess != IntPtr.Zero) NativeMethods.CloseHandle(hProcess);
        }
    }
}
