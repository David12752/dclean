# DClean — Part 2: Granular Project Plan
## Phase 3: Memory & Process Manager

---

## Phase 3.1 — Process Blacklist (Zero-Kill Zone)

This is a compile-time constant list. It is never read from config files, registries, or user input — doing so would create a bypass vector.

```csharp
// DClean.Engine/Safety/ProcessBlacklist.cs

public static class ProcessBlacklist
{
    /// <summary>
    /// Processes that must NEVER be suspended, terminated, or have their
    /// working set trimmed by DClean under any circumstances.
    /// These are OS-critical processes whose termination causes a BSOD,
    /// system halt, or data corruption.
    /// </summary>
    public static readonly IReadOnlySet<string> CriticalProcessNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // ── NT Kernel & Session Manager ─────────────────────────────────
            "System",
            "Registry",
            "smss.exe",         // Session Manager Subsystem — terminates = reboot
            "csrss.exe",        // Client/Server Runtime — terminates = BSOD
            "wininit.exe",      // Windows Initialization
            "winlogon.exe",     // Logon process
            "lsass.exe",        // Local Security Authority — terminates = reboot
            "lsaiso.exe",       // Credential Guard isolated LSA
            "services.exe",     // Service Control Manager
            "svchost.exe",      // Generic service host (block ALL instances)

            // ── Hardware abstraction / device stack ─────────────────────────
            "dwm.exe",          // Desktop Window Manager — terminates = black screen
            "fontdrvhost.exe",  // User-mode font driver — terminates = black screen

            // ── Security & Antimalware ───────────────────────────────────────
            "MsMpEng.exe",      // Windows Defender engine
            "SecurityHealthService.exe",
            "SgrmBroker.exe",   // System Guard Runtime Monitor

            // ── DClean itself ────────────────────────────────────────────────
            "DClean.Service.exe",
            "DClean.UI.exe",
        };

    /// <summary>
    /// Processes that are protected by PatchGuard/PPL (Protected Process Light)
    /// and where OpenProcess will succeed but TerminateProcess will return
    /// ERROR_ACCESS_DENIED even from LocalSystem. Listed here for diagnostic clarity.
    /// </summary>
    public static readonly IReadOnlySet<string> ProtectedProcessNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "audiodg.exe",
            "WerFault.exe",
            "spoolsv.exe",
        };

    public static bool IsCritical(string processName) =>
        CriticalProcessNames.Contains(processName) ||
        CriticalProcessNames.Contains(Path.GetFileName(processName));
}
```

---

## Phase 3.2 — ProcessEnumerator

Uses `EnumProcesses` + `OpenProcess` + `GetProcessMemoryInfo` via CsWin32. This avoids WMI (which has a 1-2 second startup overhead and requires DCOM).

```csharp
// DClean.Engine/Memory/ProcessEnumerator.cs

public sealed class ProcessEnumerator
{
    private readonly ILogger<ProcessEnumerator> _log;
    public ProcessEnumerator(ILogger<ProcessEnumerator> log) => _log = log;

    public IReadOnlyList<ProcessInfo> GetAll()
    {
        var results = new List<ProcessInfo>();

        // ── Enumerate PIDs ───────────────────────────────────────────────────
        uint[] pids = new uint[1024];
        uint bytesReturned;
        unsafe
        {
            fixed (uint* p = pids)
            {
                // CsWin32-generated: PInvoke.EnumProcesses(p, (uint)(pids.Length * 4), out bytesReturned)
                if (!PInvoke.EnumProcesses(p, (uint)(pids.Length * sizeof(uint)), out bytesReturned))
                    return results;
            }
        }
        int count = (int)(bytesReturned / sizeof(uint));

        for (int i = 0; i < count; i++)
        {
            uint pid = pids[i];
            if (pid == 0 || pid == 4) continue; // Idle and System pseudo-processes

            SafeHandle? hProcess = null;
            try
            {
                // PROCESS_QUERY_LIMITED_INFORMATION is sufficient for name + memory
                // and works on more processes than PROCESS_ALL_ACCESS
                hProcess = PInvoke.OpenProcess(
                    PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION |
                    PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ,
                    bInheritHandle: false, pid);

                if (hProcess.IsInvalid) continue;

                // ── Get image name ───────────────────────────────────────────
                string name = GetProcessName(hProcess, pid);

                // ── Get memory counters ──────────────────────────────────────
                var memInfo = GetMemoryInfo(hProcess);

                // ── PPL / protection level check ─────────────────────────────
                bool isProtected = IsProtectedProcess(hProcess);

                results.Add(new ProcessInfo(
                    Pid: (int)pid,
                    Name: name,
                    WorkingSetBytes: (long)memInfo.WorkingSetSize,
                    PrivateBytesKb: (long)(memInfo.PrivateUsage / 1024),
                    IsCritical: ProcessBlacklist.IsCritical(name),
                    IsProtected: isProtected || ProcessBlacklist.ProtectedProcessNames.Contains(name)
                ));
            }
            catch (Exception ex)
            {
                _log.LogDebug("Skipping PID {Pid}: {Msg}", pid, ex.Message);
            }
            finally
            {
                hProcess?.Dispose();
            }
        }

        return results.OrderByDescending(p => p.WorkingSetBytes).ToList();
    }

    private static unsafe string GetProcessName(SafeHandle hProcess, uint pid)
    {
        char[] buf = new char[1024];
        uint size = (uint)buf.Length;
        fixed (char* p = buf)
        {
            // QueryFullProcessImageName gives the full path; handle WOW64 correctly
            PInvoke.QueryFullProcessImageName(hProcess, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32,
                p, ref size);
        }
        return Path.GetFileName(new string(buf, 0, (int)size));
    }

    private static unsafe PROCESS_MEMORY_COUNTERS_EX GetMemoryInfo(SafeHandle hProcess)
    {
        var info = new PROCESS_MEMORY_COUNTERS_EX
        {
            cb = (uint)sizeof(PROCESS_MEMORY_COUNTERS_EX)
        };
        PInvoke.GetProcessMemoryInfo(hProcess, (PROCESS_MEMORY_COUNTERS*)&info, info.cb);
        return info;
    }

    private static unsafe bool IsProtectedProcess(SafeHandle hProcess)
    {
        // PROCESS_PROTECTION_LEVEL_INFORMATION — available via NtQueryInformationProcess
        // Returns true if ProtectionLevel > PROTECTION_LEVEL_NONE
        // Simplified: try VirtualQueryEx; if it fails with ACCESS_DENIED the process is likely PPL
        return false; // Full implementation uses NtQueryInformationProcess(ProcessProtectionInformation)
    }
}

public record ProcessInfo(
    int Pid,
    string Name,
    long WorkingSetBytes,
    long PrivateBytesKb,
    bool IsCritical,
    bool IsProtected);
```

---

## Phase 3.3 — Working Set Trimmer

**Trimming the working set** (releasing pages from RAM back to the standby list) is a zero-risk operation: it does not terminate processes, does not touch disk data, and the OS will simply re-page the memory back in if the process needs it. This is the safest "memory optimization" action.

```csharp
// DClean.Engine/Memory/WorkingSetTrimmer.cs

public sealed class WorkingSetTrimmer
{
    private readonly DryRunGuard _guard;
    private readonly ILogger<WorkingSetTrimmer> _log;

    public WorkingSetTrimmer(DryRunGuard guard, ILogger<WorkingSetTrimmer> log)
    {
        _guard = guard; _log = log;
    }

    /// <summary>
    /// Calls EmptyWorkingSet on the target process.
    /// This is equivalent to what RAMMap's "Empty Working Sets" button does.
    /// Does NOT kill or suspend the process.
    /// </summary>
    public TrimResult Trim(ProcessInfo proc)
    {
        if (proc.IsCritical)
            return new TrimResult(proc.Pid, proc.Name, false, "Critical process — blocked");

        if (!_guard.IsExecuteMode)
        {
            _log.LogInformation("[DRY-RUN] Would trim working set of {Name} (PID {Pid})", proc.Name, proc.Pid);
            return new TrimResult(proc.Pid, proc.Name, true, "DryRun");
        }

        SafeHandle? hProcess = null;
        try
        {
            hProcess = PInvoke.OpenProcess(
                PROCESS_ACCESS_RIGHTS.PROCESS_SET_QUOTA |
                PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION,
                false, (uint)proc.Pid);

            if (hProcess.IsInvalid)
                return new TrimResult(proc.Pid, proc.Name, false,
                    $"OpenProcess failed: {Marshal.GetLastWin32Error()}");

            // SetProcessWorkingSetSize(-1, -1) = "trim to minimum"
            // EmptyWorkingSet is the cleaner API (Vista+)
            bool ok = PInvoke.EmptyWorkingSet(hProcess);

            return ok
                ? new TrimResult(proc.Pid, proc.Name, true, string.Empty)
                : new TrimResult(proc.Pid, proc.Name, false,
                    $"EmptyWorkingSet failed: {Marshal.GetLastWin32Error()}");
        }
        finally { hProcess?.Dispose(); }
    }
}

public record TrimResult(int Pid, string ProcessName, bool Success, string Detail);
```

---

## Phase 3.4 — Safe Process Terminator

Termination must be a **two-stage** process: graceful first, forced only as a deliberate second step that requires explicit re-confirmation.

```csharp
// DClean.Engine/Memory/SafeTerminator.cs

public sealed class SafeTerminator
{
    private readonly DryRunGuard _guard;
    private readonly ILogger<SafeTerminator> _log;

    // Milliseconds to wait for graceful exit after WM_CLOSE
    private const int GracefulTimeoutMs = 5000;

    public SafeTerminator(DryRunGuard guard, ILogger<SafeTerminator> log)
    {
        _guard = guard; _log = log;
    }

    public async Task<TerminateResult> TerminateAsync(
        ProcessInfo proc,
        bool forceIfNeeded,
        CancellationToken ct = default)
    {
        // ── Hard block on critical processes ────────────────────────────────
        if (proc.IsCritical || proc.IsProtected)
        {
            _log.LogWarning("Termination BLOCKED for critical/protected process {Name} (PID {Pid})",
                proc.Name, proc.Pid);
            return new TerminateResult(proc.Pid, proc.Name, false, "Blocked: critical or protected");
        }

        if (!_guard.IsExecuteMode)
        {
            _log.LogInformation("[DRY-RUN] Would terminate {Name} (PID {Pid})", proc.Name, proc.Pid);
            return new TerminateResult(proc.Pid, proc.Name, true, "DryRun");
        }

        // ── Stage 1: Graceful termination via WM_CLOSE ───────────────────────
        // PostMessage to all top-level windows owned by the process.
        // This gives the application a chance to save data and exit cleanly.
        bool sentClose = SendCloseMessages(proc.Pid);
        if (sentClose)
        {
            // Wait for the process to exit gracefully
            bool exited = await WaitForExitAsync(proc.Pid, GracefulTimeoutMs, ct);
            if (exited)
            {
                DCleanEventSource.Log.ProcessTerminated(proc.Pid, proc.Name, wasGraceful: true);
                return new TerminateResult(proc.Pid, proc.Name, true, "GracefulExit");
            }
        }

        // ── Stage 2: Force-terminate (only if caller explicitly requested) ───
        if (!forceIfNeeded)
        {
            _log.LogWarning("{Name} (PID {Pid}) did not exit gracefully. Force-kill NOT requested.",
                proc.Name, proc.Pid);
            return new TerminateResult(proc.Pid, proc.Name, false, "DidNotExitGracefully_NoForce");
        }

        return ForceTerminate(proc);
    }

    private static bool SendCloseMessages(int pid)
    {
        // EnumWindows to find all top-level windows owned by this PID
        bool anySent = false;
        PInvoke.EnumWindows((hWnd, _) =>
        {
            PInvoke.GetWindowThreadProcessId(hWnd, out uint winPid);
            if (winPid == (uint)pid)
            {
                // WM_CLOSE = 0x0010; PostMessage is async (non-blocking)
                PInvoke.PostMessage(hWnd, PInvoke.WM_CLOSE, 0, 0);
                anySent = true;
            }
            return true; // continue enumeration
        }, 0);
        return anySent;
    }

    private static async Task<bool> WaitForExitAsync(int pid, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return await proc.WaitForExitAsync(ct)
                .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct)
                .ContinueWith(t => !t.IsFaulted && !t.IsCanceled, ct);
        }
        catch (ArgumentException) { return true; } // Process already gone
        catch { return false; }
    }

    private TerminateResult ForceTerminate(ProcessInfo proc)
    {
        SafeHandle? hProcess = null;
        try
        {
            hProcess = PInvoke.OpenProcess(
                PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE, false, (uint)proc.Pid);

            if (hProcess.IsInvalid)
                return new TerminateResult(proc.Pid, proc.Name, false,
                    $"OpenProcess failed: {Marshal.GetLastWin32Error()}");

            bool ok = PInvoke.TerminateProcess(hProcess, exitCode: 1);
            DCleanEventSource.Log.ProcessTerminated(proc.Pid, proc.Name, wasGraceful: false);

            return ok
                ? new TerminateResult(proc.Pid, proc.Name, true, "ForcedTerminate")
                : new TerminateResult(proc.Pid, proc.Name, false,
                    $"TerminateProcess failed: {Marshal.GetLastWin32Error()}");
        }
        finally { hProcess?.Dispose(); }
    }
}

public record TerminateResult(int Pid, string ProcessName, bool Success, string Detail);
```

### Why `WM_CLOSE` first?

Calling `TerminateProcess` directly on a process like Notepad with an unsaved file will lose the user's data silently. `WM_CLOSE` triggers the application's own save-dialog. Only after the graceful timeout expires — and only if the user explicitly checked "force if needed" — do we call `TerminateProcess`. This two-stage design is consistent with how Task Manager itself works (End Task vs. End Process Tree).

### PID Recycling Defense

Between the time `ProcessEnumerator.GetAll()` returns and `SafeTerminator.TerminateAsync()` executes, the target PID could have been recycled by the OS for a new — potentially critical — process. Defense:

```csharp
// After OpenProcess, verify the image name still matches before terminating
string currentName = GetProcessName(hProcess, (uint)proc.Pid);
if (!string.Equals(currentName, proc.Name, StringComparison.OrdinalIgnoreCase))
{
    _log.LogWarning("PID {Pid} recycled! Was '{OldName}', now '{NewName}'. Aborting kill.",
        proc.Pid, proc.Name, currentName);
    return new TerminateResult(proc.Pid, proc.Name, false, "PidRecycled");
}
```

---

## Phase 3.5 — Real-Time RAM Monitor

Use a `PerformanceCounter` for system-wide available RAM, sampled on a timer. The `PerformanceCounter` approach is lighter than WMI for periodic polling.

```csharp
// DClean.Engine/Memory/RamMonitor.cs

public sealed class RamMonitor : IDisposable
{
    // "Memory" / "Available MBytes" counter is updated every ~1 second by the OS
    private readonly PerformanceCounter _availableRam =
        new("Memory", "Available MBytes");

    private readonly PerformanceCounter _committedBytes =
        new("Memory", "% Committed Bytes In Use");

    public RamSnapshot GetSnapshot()
    {
        // GetTotalPhysicalMemory via GlobalMemoryStatusEx
        PInvoke.GlobalMemoryStatusEx(out var memStatus);

        return new RamSnapshot(
            TotalBytes:     (long)memStatus.ullTotalPhys,
            AvailableBytes: (long)memStatus.ullAvailPhys,
            UsedPercent:    memStatus.dwMemoryLoad,
            CommitChargePercent: (float)_committedBytes.NextValue()
        );
    }

    public void Dispose() { _availableRam.Dispose(); _committedBytes.Dispose(); }
}

public record RamSnapshot(
    long TotalBytes,
    long AvailableBytes,
    uint UsedPercent,
    float CommitChargePercent);
```
