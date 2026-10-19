# DClean — Part 2: Granular Project Plan
## Phase 1: Foundation & Security

---

## Phase 1.1 — Solution Structure

```
DClean.sln
├── src/
│   ├── DClean.UI/                  # WPF host (standard user)
│   │   ├── App.xaml
│   │   ├── Views/
│   │   ├── ViewModels/
│   │   ├── Services/               # IPC client wrapper
│   │   └── DClean.UI.csproj
│   ├── DClean.Service/             # Windows Service host (LocalSystem)
│   │   ├── Worker.cs               # BackgroundService entry point
│   │   ├── PipeServer.cs
│   │   ├── DClean.Service.csproj
│   │   └── app.manifest
│   ├── DClean.Engine/              # Class library — pure logic, no UI, no I/O side effects at the type level
│   │   ├── Safety/
│   │   │   ├── PathValidator.cs
│   │   │   ├── ProcessBlacklist.cs
│   │   │   └── DryRunGuard.cs
│   │   ├── Cleaning/
│   │   │   ├── FileScanner.cs
│   │   │   ├── LockedFileHandler.cs
│   │   │   ├── BrowserCacheLocator.cs
│   │   │   └── SafeDeleter.cs
│   │   ├── Memory/
│   │   │   ├── ProcessEnumerator.cs
│   │   │   ├── WorkingSetTrimmer.cs
│   │   │   └── SafeTerminator.cs
│   │   └── DClean.Engine.csproj
│   └── DClean.Contracts/           # Shared protobuf types + IPC interfaces
│       ├── Protos/command.proto
│       └── DClean.Contracts.csproj
└── tests/
    ├── DClean.Engine.Tests/
    └── DClean.Service.Tests/
```

**Key principle:** `DClean.Engine` has **no dependency** on `System.IO` concrete types — all file system operations are injected through `IFileSystem` and `IProcessInfoProvider` interfaces. This enables full unit-testability without touching the real filesystem or real processes.

---

## Phase 1.2 — Project Files & Manifests

### DClean.Service — app.manifest
```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="DClean.Service"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security>
      <requestedPrivileges>
        <!-- Service runs as LocalSystem; manifest just documents intent -->
        <requestedExecutionLevel level="requireAdministrator" uiAccess="false"/>
      </requestedPrivileges>
    </security>
  </trustInfo>
  <!-- Opt into long path awareness (paths > MAX_PATH) -->
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
    </windowsSettings>
  </application>
</assembly>
```

### DClean.UI — app.manifest
```xml
<requestedExecutionLevel level="asInvoker" uiAccess="false"/>
```
The UI **never** asks for elevation. The service already holds the needed privilege.

### DClean.Engine.csproj — CsWin32 setup
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>  <!-- Required for some P/Invoke patterns -->
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Windows.CsWin32" Version="0.3.*" PrivateAssets="all"/>
    <PackageReference Include="Google.Protobuf"           Version="3.*"/>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="8.*"/>
  </ItemGroup>
  <!-- CsWin32: declare the Win32 functions you need in NativeMethods.txt -->
</Project>
```

Create `NativeMethods.txt` in `DClean.Engine/`:
```
EnumProcesses
OpenProcess
GetProcessMemoryInfo
QueryFullProcessImageName
TerminateProcess
SetProcessWorkingSetSize
EmptyWorkingSet
DeleteFile
MoveFileEx
DeviceIoControl
GetFileAttributes
GetFileInformationByHandleEx
NtQueryInformationFile
RtlNtStatusToDosError
AdjustTokenPrivileges
OpenProcessToken
LookupPrivilegeValue
```

---

## Phase 1.3 — Dry-Run State Machine

This is the most critical safety mechanism. It is NOT a boolean flag. It is an explicit state machine with a mandatory confirmation token.

```csharp
// DClean.Engine/Safety/DryRunGuard.cs

public enum ExecutionMode { DryRun, AwaitingConfirmation, Execute }

public sealed class DryRunGuard
{
    private ExecutionMode _mode = ExecutionMode.DryRun;
    private string? _confirmationToken;
    private readonly ILogger<DryRunGuard> _log;

    public DryRunGuard(ILogger<DryRunGuard> log) => _log = log;

    /// <summary>Called by UI after user views the dry-run report and clicks "Proceed".</summary>
    public string RequestExecution()
    {
        _mode = ExecutionMode.AwaitingConfirmation;
        _confirmationToken = Guid.NewGuid().ToString("N");
        _log.LogWarning("Execution requested. Confirmation token issued: {Token}", _confirmationToken);
        return _confirmationToken;
    }

    /// <summary>UI must echo the token back, proving it came from the same session.</summary>
    public bool ConfirmExecution(string token)
    {
        if (_mode != ExecutionMode.AwaitingConfirmation ||
            !string.Equals(token, _confirmationToken, StringComparison.Ordinal))
        {
            _log.LogError("Execution confirmation FAILED. Resetting to DryRun.");
            Reset();
            return false;
        }
        _mode = ExecutionMode.Execute;
        _confirmationToken = null;
        _log.LogWarning("Execution CONFIRMED. Mode is now Execute.");
        return true;
    }

    public bool IsExecuteMode => _mode == ExecutionMode.Execute;

    /// <summary>Always called after a run completes — success or failure.</summary>
    public void Reset() { _mode = ExecutionMode.DryRun; _confirmationToken = null; }
}
```

---

## Phase 1.4 — Windows Service Registration

```csharp
// DClean.Service/Program.cs
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(o => o.ServiceName = "DCleanService")
    .ConfigureServices(services =>
    {
        services.AddSingleton<DryRunGuard>();
        services.AddSingleton<PathValidator>();
        services.AddSingleton<ProcessBlacklist>();
        services.AddSingleton<FileScanner>();
        services.AddSingleton<SafeDeleter>();
        services.AddSingleton<SafeTerminator>();
        services.AddSingleton<PipeServer>();
        services.AddHostedService<Worker>();
    })
    .Build();

await host.RunAsync();
```

**Service installation command (run once, during setup):**
```powershell
sc.exe create DCleanService binPath= "C:\Program Files\DClean\DClean.Service.exe" start= demand DisplayName= "DClean Background Service"
sc.exe description DCleanService "Provides elevated file cleaning and process management for DClean."
# Restrict service to only be startable by Administrators
sc.exe sdset DCleanService "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)"
```

---

## Phase 1.5 — ETW Logging Architecture

Use `System.Diagnostics.Tracing.EventSource` for zero-allocation structured logging that integrates with PerfView, WPA, and Windows Event Log simultaneously.

```csharp
// DClean.Engine/Logging/DCleanEventSource.cs
[EventSource(Name = "DClean-Engine")]
internal sealed class DCleanEventSource : EventSource
{
    public static readonly DCleanEventSource Log = new();

    [Event(1, Level = EventLevel.Informational, Keywords = Keywords.FileOps)]
    public void FileScanEntry(string path, long sizeBytes) =>
        WriteEvent(1, path, sizeBytes);

    [Event(2, Level = EventLevel.Warning, Keywords = Keywords.FileOps)]
    public void FileSkipped(string path, string reason) =>
        WriteEvent(2, path, reason);

    [Event(3, Level = EventLevel.Critical, Keywords = Keywords.Safety)]
    public void SafetyGateBlocked(string path, string rule) =>
        WriteEvent(3, path, rule);

    [Event(4, Level = EventLevel.Informational, Keywords = Keywords.Process)]
    public void ProcessTerminated(int pid, string name, bool wasGraceful) =>
        WriteEvent(4, pid, name, wasGraceful);

    public static class Keywords
    {
        public const EventKeywords FileOps = (EventKeywords)0x1;
        public const EventKeywords Safety  = (EventKeywords)0x2;
        public const EventKeywords Process = (EventKeywords)0x4;
    }
}
```

Every safety gate block (`SafetyGateBlocked`) is logged at `Critical` level and written to the **Windows Application Event Log** as well, creating a permanent audit trail.
