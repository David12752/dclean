# DClean — Appendices

---

## Appendix A — Hardcoded Safety Manifests

### A.1 Complete Forbidden Path Prefix List

These paths are compiled into `PathValidator` as constants. They are NEVER read from configuration files, registry, or user input. Modification requires a code change and a new signed build.

```
# NT Kernel & Core OS
C:\Windows\System32
C:\Windows\SysWOW64
C:\Windows\WinSxS
C:\Windows\servicing
C:\Windows\Boot
C:\Windows\Fonts
C:\Windows\INF
C:\Windows\assembly
C:\Windows\Microsoft.NET
C:\Windows\SystemApps
C:\Windows\ImmersiveControlPanel
C:\Windows\Panther           # Windows Setup logs — do not touch
C:\Windows\SoftwareDistribution\Download  # Windows Update cache — managed by WU
C:\Windows\LiveKernelReports
C:\Windows\Minidump

# Program directories
C:\Program Files
C:\Program Files (x86)

# User profile sensitive areas
C:\Users\Default
C:\Users\Public\Desktop
C:\ProgramData\Microsoft\Windows\Start Menu
C:\ProgramData\Microsoft\Windows Defender

# Recovery & Boot
C:\Recovery
C:\$Recycle.Bin
C:\$WinREAgent
C:\System Volume Information
C:\EFI
C:\bootmgr
C:\BOOTNXT
C:\pagefile.sys
C:\hiberfil.sys
C:\swapfile.sys
C:\DumpStack.log.tmp
```

### A.2 Complete Process Blacklist

```
# Session / Subsystem
System
Registry
Idle
smss.exe
csrss.exe
wininit.exe
winlogon.exe
fontdrvhost.exe

# Security / Authentication
lsass.exe
lsaiso.exe
services.exe

# Service Host (ALL instances are protected)
svchost.exe

# Shell & Desktop
dwm.exe
explorer.exe        # Included: killing explorer is non-fatal but destructive to UX
sihost.exe          # Shell Infrastructure Host
ShellExperienceHost.exe
StartMenuExperienceHost.exe

# Security Products
MsMpEng.exe
SecurityHealthService.exe
SgrmBroker.exe

# DClean itself (self-protection)
DClean.Service.exe
DClean.UI.exe
```

### A.3 Allowed Cleaning Roots (Dynamic — Built at Runtime)

```
%TEMP%                                  → C:\Users\<User>\AppData\Local\Temp
%WINDIR%\Temp                           → C:\Windows\Temp
%LOCALAPPDATA%\Temp
%LOCALAPPDATA%\Google\Chrome\User Data\Default\Cache
%LOCALAPPDATA%\Google\Chrome\User Data\Default\Code Cache
%LOCALAPPDATA%\Google\Chrome\User Data\Default\GPUCache
%LOCALAPPDATA%\Microsoft\Edge\User Data\Default\Cache
%LOCALAPPDATA%\Microsoft\Edge\User Data\Default\Code Cache
%LOCALAPPDATA%\Mozilla\Firefox\Profiles\<profile>\cache2
%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data\Default\Cache
%LOCALAPPDATA%\Opera Software\Opera Stable\Cache
%LOCALAPPDATA%\Vivaldi\User Data\Default\Cache
%LOCALAPPDATA%\Packages\*\AC\Temp       # UWP app temp directories
%LOCALAPPDATA%\CrashDumps               # User-mode crash dumps (safe to clear)
%LOCALAPPDATA%\Microsoft\Windows\INetCache     # IE/Edge Legacy cache
%LOCALAPPDATA%\Microsoft\Windows\WebCache      # Only the .dat files, not the folder itself
```

---

## Appendix B — Windows API Quick Reference

### B.1 File Operations

| Task | Win32 API | .NET Equivalent | Notes |
|---|---|---|---|
| Delete file | `DeleteFile` | `File.Delete` | Returns `ERROR_SHARING_VIOLATION` if locked |
| Schedule delete on reboot | `MoveFileEx(path, NULL, MOVEFILE_DELAY_UNTIL_REBOOT)` | No equivalent | Writes to `PendingFileRenameOperations` |
| Get file attributes | `GetFileAttributes` | `FileInfo.Attributes` | Check `FILE_ATTRIBUTE_REPARSE_POINT` |
| Get real path (resolve symlink) | `GetFinalPathNameByHandle` | `FileInfo.ResolveLinkTarget(true)` | .NET 6+ only |
| Enumerate files | `FindFirstFileEx` | `Directory.EnumerateFiles` | Use `EnumerationOptions.IgnoreInaccessible` |
| Get file size without opening | `GetFileAttributesEx` | `FileInfo.Length` | More efficient than `CreateFile` |
| Check if path is a junction | `DeviceIoControl(FSCTL_GET_REPARSE_POINT)` | Check `FileAttributes.ReparsePoint` | Always skip junctions during traversal |
| Get volume info (drive space) | `GetDiskFreeSpaceEx` | `DriveInfo.AvailableFreeSpace` | Use for "X GB free" display |

### B.2 Process Operations

| Task | Win32 API | Notes |
|---|---|---|
| List all PIDs | `EnumProcesses` | Returns array of DWORDs; allocate larger buffer and retry if `bytesReturned == bufferSize` |
| Get process handle | `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` | Prefer LIMITED over ALL_ACCESS; works on more processes |
| Get full image path | `QueryFullProcessImageName(PROCESS_NAME_WIN32)` | Handles WOW64 correctly; prefer over `GetModuleFileNameEx` |
| Get memory info | `GetProcessMemoryInfo` → `PROCESS_MEMORY_COUNTERS_EX` | `PrivateUsage` = private bytes; `WorkingSetSize` = RAM used |
| Trim working set | `EmptyWorkingSet(hProcess)` | Zero-risk; OS re-pages on demand |
| Graceful exit | `PostMessage(hWnd, WM_CLOSE, 0, 0)` | Non-blocking; use `EnumWindows` to find target HWNDs |
| Force kill | `TerminateProcess(hProcess, 1)` | Always verify PID not recycled first |
| Check protection level | `NtQueryInformationProcess(ProcessProtectionInformation)` | Undocumented but stable since Win8.1 |
| Get process token | `OpenProcessToken(hProcess, TOKEN_QUERY)` | For privilege and integrity level checks |

### B.3 Memory Operations

| Task | Win32 API | Notes |
|---|---|---|
| System RAM stats | `GlobalMemoryStatusEx` → `MEMORYSTATUSEX` | `ullTotalPhys`, `ullAvailPhys`, `dwMemoryLoad` |
| System commit stats | `GetPerformanceInfo` | Total committed pages, page size |
| Enable privilege | `AdjustTokenPrivileges` + `LookupPrivilegeValue` | Enable `SeDebugPrivilege` only when needed |
| Page file info | `GetSystemFileCacheSize` | For diagnostics display |

### B.4 Service & Security Operations

| Task | Win32 API / Method | Notes |
|---|---|---|
| Create named pipe with ACL | `CreateNamedPipeW` + `SetSecurityInfo` | Or use `NamedPipeServerStreamAcl.Create` (.NET) |
| Check caller identity on pipe | `GetNamedPipeClientProcessId` + `OpenProcess` | Verify the connecting PID matches the UI process |
| Impersonate pipe client | `ImpersonateNamedPipeClient` | Not needed if ACL is correct; available as defense-in-depth |
| Install Windows service | `CreateService` (SCM) | Or `sc.exe create` in PowerShell |
| Query service status | `QueryServiceStatusEx` | For UI to check if service is running |

---

## Appendix C — Known Edge Cases & Mitigations

### C.1 OneDrive Virtual Files (Cloud-only Files)

Files stored in OneDrive with "Files On-Demand" enabled have the `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` attribute. Scanning these files causes them to be downloaded from the cloud. **Always check for this attribute and skip:**

```csharp
const FileAttributes CloudOnlyMask =
    (FileAttributes)0x00400000 | // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
    (FileAttributes)0x00040000;  // FILE_ATTRIBUTE_RECALL_ON_OPEN

if ((file.Attributes & CloudOnlyMask) != 0)
{
    // Skip: touching this file triggers a cloud download
    continue;
}
```

### C.2 Long Paths (> MAX_PATH = 260 Characters)

With the `longPathAware` manifest element and `HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled = 1`, .NET 8 on Windows 10 1607+ handles paths up to 32,767 characters. Without this, `Directory.EnumerateFiles` will silently skip or throw on long-path files. The manifest entry (Phase 1.2) handles this.

### C.3 Transactional NTFS (TxF)

Do NOT use `CreateFileTransacted` or any TxF APIs. TxF is deprecated as of Windows Vista and will throw `ERROR_NOT_SUPPORTED` on storage devices that don't support it. Use standard `DeleteFile` + `MoveFileEx` for all deletion operations.

### C.4 Case-Insensitive Path Comparison

Windows NTFS is case-insensitive but case-preserving. All path comparisons in DClean use `StringComparison.OrdinalIgnoreCase`. Never use `StringComparison.Ordinal` for path matching — it will fail to block `c:\windows\system32` (lowercase) vs `C:\Windows\System32`.

### C.5 UNC Paths & Network Shares

DClean must **never** scan or delete files on UNC paths (`\\server\share\...`). The `PathValidator` implicitly blocks these because they don't start with any of the allowed `C:\...` roots. If the application is ever extended to support other drives, add an explicit UNC check:

```csharp
if (fullPath.StartsWith(@"\\") || fullPath.StartsWith("//"))
{
    reason = "UNC/network paths are not supported";
    return ValidationResult.Blocked;
}
```

### C.6 Drive Letter Aliasing (`\Device\HarddiskVolume3\...`)

`QueryFullProcessImageName` with `PROCESS_NAME_WIN32` returns Win32-style paths (`C:\...`). `QueryFullProcessImageName` with `PROCESS_NAME_NATIVE` returns NT device paths (`\Device\HarddiskVolume3\...`). Always use `PROCESS_NAME_WIN32` in DClean to ensure path comparisons against the `PathValidator` prefix list are correct. Use `QueryDosDevice` to convert NT device paths to Win32 paths if needed.

### C.7 Symbolic Link / Junction Attack (Privilege Escalation via Temp Directory)

An attacker (or malware) could create:
```
C:\Users\Victim\AppData\Local\Temp\
    malicious_link\  ← Junction pointing to C:\Windows\System32
        kernel32.dll ← Now "inside" an allowed root
```

Defense: `PathValidator.GetRealPath()` calls `FileInfo.ResolveLinkTarget(returnFinalTarget: true)` which resolves the chain to its real final destination (`C:\Windows\System32\kernel32.dll`), and the forbidden prefix check catches it.

Additionally, during directory traversal in `FileScanner`, **always check `FileAttributes.ReparsePoint` and skip** — this prevents even entering the junction directory.

### C.8 Windows Update / CBS Store Files in TEMP

Windows Update (`wuauclt.exe`, `WaasMedic.exe`) frequently creates large temporary files in `C:\Windows\SoftwareDistribution\Download` and `C:\Windows\Temp`. These files may appear to be orphaned but are actively managed by Windows Update. Never auto-delete from `C:\Windows\SoftwareDistribution\` — it is in the forbidden prefix list. For `C:\Windows\Temp`, apply the 48-hour age filter (Phase 2.5) to avoid deleting active Windows Update staging files.

---

## Appendix D — Glossary

| Term | Definition |
|---|---|
| **Dry-Run** | Scan mode where all operations are logged but no files are deleted and no processes are modified. The default and safe state. |
| **Safety Gate** | The `PathValidator` class that every path passes through before any destructive operation. Runs synchronously, before async I/O. |
| **Allowlist** | The set of root directories that DClean is permitted to clean. Everything outside this list is blocked, regardless of other conditions. |
| **Reparse Point** | A Windows filesystem object that redirects access to another location. Includes symlinks, junctions (directory symlinks), and volume mount points. DClean never follows or deletes reparse points. |
| **Working Set Trim** | Releasing a process's physical RAM pages back to the OS standby list without terminating the process. The OS re-pages memory on demand. Zero-risk operation. |
| **PID Recycling** | Windows reuses process IDs after a process exits. DClean defends against this by verifying the process name matches before terminating. |
| **PPL (Protected Process Light)** | A Windows security feature that restricts which processes can open a handle to certain processes (e.g., antimalware, audio drivers). DClean cannot kill PPL processes. |
| **ETW (Event Tracing for Windows)** | A kernel-level tracing infrastructure. DClean uses `EventSource` to emit structured events consumable by PerfView, WPA, and Windows Event Log. |
| **MSIX** | Modern Windows app packaging format. Provides clean install/uninstall, mandatory code signing, and optional Store distribution. |
| **`MoveFileEx(DELAY_UNTIL_REBOOT)`** | Win32 API that schedules a file for deletion on the next system reboot by writing to `PendingFileRenameOperations` in the registry. Used for locked files. |
| **Named Pipe ACL** | Access control list on the named pipe endpoint that restricts which users can connect. DClean's pipe only allows the interactive logged-on user. |
