# DClean — Part 2: Granular Project Plan
## Phase 5: Testing & Deployment

---

## Phase 5.1 — Testing Strategy

### Tier 1: Unit Tests (DClean.Engine.Tests)

The engine's interface-injected design (`IFileSystem`, `IProcessInfoProvider`) means every safety-critical class is fully unit-testable without touching the real OS.

```csharp
// tests/DClean.Engine.Tests/PathValidatorTests.cs

public class PathValidatorTests
{
    private readonly PathValidator _sut;

    public PathValidatorTests()
    {
        _sut = new PathValidator(NullLogger<PathValidator>.Instance);
        _sut.AddAllowedRoot(@"C:\Users\TestUser\AppData\Local\Temp");
    }

    // ── Forbidden prefix tests ────────────────────────────────────────────
    [Theory]
    [InlineData(@"C:\Windows\System32\kernel32.dll")]
    [InlineData(@"C:\Windows\SysWOW64\ntdll.dll")]
    [InlineData(@"C:\Windows\WinSxS\amd64_microsoft.windows.common-controls_abc\comctl32.dll")]
    [InlineData(@"C:\Program Files\SomeApp\app.exe")]
    [InlineData(@"C:\pagefile.sys")]
    [InlineData(@"C:\hiberfil.sys")]
    [InlineData(@"C:\Recovery\WindowsRE\winre.wim")]
    public void Validate_ForbiddenPaths_ReturnBlocked(string path)
    {
        var result = _sut.Validate(path, out var reason);
        Assert.Equal(ValidationResult.Blocked, result);
        Assert.NotEmpty(reason);
    }

    // ── Forbidden extension tests ─────────────────────────────────────────
    [Theory]
    [InlineData(@"C:\Users\TestUser\AppData\Local\Temp\setup.exe")]
    [InlineData(@"C:\Users\TestUser\AppData\Local\Temp\helper.dll")]
    [InlineData(@"C:\Users\TestUser\AppData\Local\Temp\driver.sys")]
    public void Validate_ForbiddenExtensions_ReturnBlocked(string path)
    {
        var result = _sut.Validate(path, out _);
        Assert.Equal(ValidationResult.Blocked, result);
    }

    // ── Allowed path tests ────────────────────────────────────────────────
    [Theory]
    [InlineData(@"C:\Users\TestUser\AppData\Local\Temp\abc123.tmp")]
    [InlineData(@"C:\Users\TestUser\AppData\Local\Temp\subfolder\cache.dat")]
    public void Validate_AllowedPaths_ReturnAllowed(string path)
    {
        var result = _sut.Validate(path, out _);
        Assert.Equal(ValidationResult.Allowed, result);
    }

    // ── Not-in-allowlist test ─────────────────────────────────────────────
    [Fact]
    public void Validate_PathNotInAllowedRoot_ReturnBlocked()
    {
        var result = _sut.Validate(@"C:\Users\TestUser\Documents\report.tmp", out var reason);
        Assert.Equal(ValidationResult.Blocked, result);
        Assert.Contains("allowed root", reason, StringComparison.OrdinalIgnoreCase);
    }
}
```

```csharp
// tests/DClean.Engine.Tests/DryRunGuardTests.cs

public class DryRunGuardTests
{
    [Fact]
    public void DefaultMode_IsDryRun()
    {
        var guard = new DryRunGuard(NullLogger<DryRunGuard>.Instance);
        Assert.False(guard.IsExecuteMode);
    }

    [Fact]
    public void ConfirmExecution_WithCorrectToken_SetsExecuteMode()
    {
        var guard = new DryRunGuard(NullLogger<DryRunGuard>.Instance);
        var token = guard.RequestExecution();
        bool confirmed = guard.ConfirmExecution(token);
        Assert.True(confirmed);
        Assert.True(guard.IsExecuteMode);
    }

    [Fact]
    public void ConfirmExecution_WithWrongToken_RemainsBlocked()
    {
        var guard = new DryRunGuard(NullLogger<DryRunGuard>.Instance);
        guard.RequestExecution();
        bool confirmed = guard.ConfirmExecution("wrong-token-000");
        Assert.False(confirmed);
        Assert.False(guard.IsExecuteMode);
    }

    [Fact]
    public void Reset_AfterExecution_ReturnsToDryRun()
    {
        var guard = new DryRunGuard(NullLogger<DryRunGuard>.Instance);
        var token = guard.RequestExecution();
        guard.ConfirmExecution(token);
        guard.Reset();
        Assert.False(guard.IsExecuteMode);
    }
}
```

```csharp
// tests/DClean.Engine.Tests/SafeTerminatorTests.cs

public class SafeTerminatorTests
{
    [Theory]
    [InlineData("csrss.exe")]
    [InlineData("smss.exe")]
    [InlineData("lsass.exe")]
    [InlineData("services.exe")]
    [InlineData("winlogon.exe")]
    [InlineData("System")]
    public async Task Terminate_CriticalProcess_IsAlwaysBlocked(string processName)
    {
        var guard = new DryRunGuard(NullLogger<DryRunGuard>.Instance);
        var terminator = new SafeTerminator(guard, NullLogger<SafeTerminator>.Instance);

        var proc = new ProcessInfo(Pid: 9999, Name: processName,
            WorkingSetBytes: 0, PrivateBytesKb: 0,
            IsCritical: ProcessBlacklist.IsCritical(processName),
            IsProtected: false);

        var result = await terminator.TerminateAsync(proc, forceIfNeeded: true);
        Assert.False(result.Success);
        Assert.Contains("Blocked", result.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
```

### Tier 2: Integration Tests (VM-Based)

All integration tests MUST run inside a **Hyper-V checkpoint** or **Windows Sandbox**. The test harness:

1. Creates a snapshot before the test suite runs.
2. Runs the full engine against real temp directories containing synthetic junk files.
3. Verifies only the expected files were deleted.
4. Reverts the snapshot if any unexpected deletion is detected (canary file check).

**Canary File Pattern:**
```csharp
// Place a file OUTSIDE the allowed roots before each integration test.
// If it's gone after the test, the safety gate failed — hard fail.
var canaryPath = @"C:\Windows\Temp\DCLEAN_CANARY_DO_NOT_DELETE.txt";
File.WriteAllText(canaryPath, "canary");
// ... run engine ...
Assert.True(File.Exists(canaryPath), "SAFETY GATE FAILURE: canary file was deleted!");
File.Delete(canaryPath); // cleanup
```

### Tier 3: Fuzz Testing (Symlink Attack Simulation)

```powershell
# Create a malicious junction inside %TEMP% pointing to System32
# Run this in a VM ONLY. Tests that PathValidator blocks it.
$target = "$env:TEMP\evil_junction"
New-Item -ItemType Junction -Path $target -Target "C:\Windows\System32"
# DClean scan should report all files under $target as BLOCKED, not scan/delete them
```

---

## Phase 5.2 — VM Testing Matrix

| Test Environment | Purpose |
|---|---|
| Windows 11 22H2 (clean install) | Primary target; validate all features |
| Windows 10 22H2 | Backward compatibility check |
| Windows 11 with OneDrive sync enabled | Verify OneDrive virtual files (`.lnk`/reparse) are not deleted |
| Windows 11 Enterprise (domain-joined) | Verify Group Policy doesn't block named pipe |
| Windows Sandbox | Rapid iteration during active development |
| Hyper-V Gen2 VM with checkpoints | Integration test harness with rollback |

**Windows Sandbox** (`windowssandbox.exe`) is ideal during Phase 2/3 development: it provides a fresh, isolated Windows instance in ~2 seconds with no persistent state. Create a `.wsb` config to auto-mount the project directory:

```xml
<!-- DClean.wsb -->
<Configuration>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>C:\Dev\DClean\bin\Release\net8.0-windows</HostFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>C:\Users\WDAGUtilityAccount\Desktop\DClean\DClean.UI.exe</Command>
  </LogonCommand>
</Configuration>
```

---

## Phase 5.3 — MSIX Packaging

MSIX is the preferred modern Windows packaging format. It provides clean install/uninstall (no registry leftovers), automatic updates via the Microsoft Store or sideloading, and tamper-evident signing.

```xml
<!-- Package.appxmanifest (simplified) -->
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">

  <Identity Name="DClean.App"
            Publisher="CN=YourPublisher"
            Version="1.0.0.0"
            ProcessorArchitecture="x64"/>

  <Properties>
    <DisplayName>DClean — ניקוי ומנהל משאבים</DisplayName>
    <PublisherDisplayName>Your Name</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>

  <Capabilities>
    <!-- Required for LocalSystem service and process management -->
    <rescap:Capability Name="runFullTrust"/>
  </Capabilities>

  <Applications>
    <Application Id="DClean" Executable="DClean.UI.exe"
                 EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="DClean"
                          Description="ניקוי ומנהל משאבים"
                          BackgroundColor="transparent"
                          Square150x150Logo="Assets\Square150x150Logo.png"
                          Square44x44Logo="Assets\Square44x44Logo.png"/>
    </Application>
  </Applications>

  <!-- Service registration via Extensions -->
  <Extensions>
    <uap:Extension Category="windows.fullTrustProcess"
                   Executable="DClean.Service.exe"/>
  </Extensions>
</Package>
```

**Code Signing:** The MSIX must be signed with an EV (Extended Validation) code signing certificate. Without it, Windows SmartScreen will block the install. For development/testing: use a self-signed cert with `New-SelfSignedCertificate` and install it to Trusted Root.

```powershell
# Development self-signed signing pipeline
$cert = New-SelfSignedCertificate -Type CodeSigningCert `
    -Subject "CN=DClean Dev" -CertStoreLocation Cert:\CurrentUser\My
$pfxPath = "$env:TEMP\DClean_Dev.pfx"
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password (ConvertTo-SecureString "devpass" -AsPlainText -Force)
# In your build pipeline:
# signtool.exe sign /fd SHA256 /p7 . /p7co 1.2.840.113549.1.7.1 /p7ce DetachedSignedData /f DClean_Dev.pfx /p devpass DClean.msix
```

---

## Phase 5.4 — Build Pipeline (GitHub Actions / Azure DevOps)

```yaml
# .github/workflows/build.yml
name: DClean Build & Test

on: [push, pull_request]

jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET 8
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Restore
        run: dotnet restore DClean.sln

      - name: Build (Release)
        run: dotnet build DClean.sln -c Release --no-restore

      - name: Unit Tests
        run: dotnet test tests/DClean.Engine.Tests --no-build -c Release
             --logger "trx;LogFileName=test-results.trx"
             --collect:"XPlat Code Coverage"

      - name: Publish Coverage
        uses: codecov/codecov-action@v4

      - name: Publish UI
        run: dotnet publish src/DClean.UI -c Release -r win-x64
             --self-contained false -o ./publish/ui

      - name: Publish Service
        run: dotnet publish src/DClean.Service -c Release -r win-x64
             --self-contained false -o ./publish/service
```

---

## Phase 5.5 — Service Installation Script (Setup)

For non-MSIX distribution, provide a PowerShell installer:

```powershell
# install.ps1 — Run as Administrator

param(
    [string]$InstallDir = "C:\Program Files\DClean"
)

$ServiceName = "DCleanService"
$ServiceExe  = Join-Path $InstallDir "DClean.Service.exe"

Write-Host "Installing DClean to $InstallDir..."

# Copy files
Copy-Item -Recurse -Force ".\publish\*" $InstallDir

# Stop and remove existing service if present
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# Install new service
New-Service -Name $ServiceName `
    -BinaryPathName "`"$ServiceExe`"" `
    -DisplayName "DClean Background Service" `
    -Description "Provides elevated file cleaning and process management for DClean." `
    -StartupType Manual  # Demand-start only; UI starts it when needed

# Restrict service control to Administrators only
sc.exe sdset $ServiceName "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)"

# Create desktop shortcut
$Shell = New-Object -ComObject WScript.Shell
$Shortcut = $Shell.CreateShortcut("$env:PUBLIC\Desktop\DClean.lnk")
$Shortcut.TargetPath = Join-Path $InstallDir "DClean.UI.exe"
$Shortcut.WorkingDirectory = $InstallDir
$Shortcut.Save()

Write-Host "Installation complete." -ForegroundColor Green
```

---

## Phase 5.6 — Version Numbering & Update Strategy

Use **Semantic Versioning** (`MAJOR.MINOR.PATCH`):

- `PATCH`: bug fixes, safety rule additions, no new features
- `MINOR`: new cleaning targets, new process management features  
- `MAJOR`: architectural changes, IPC protocol breaking changes

For updates:
- The UI checks a `version.json` endpoint on startup (configurable, opt-in).
- The update package is always a full replacement MSIX — no delta patching, to avoid partial-update states that could leave an inconsistent safety gate.

---

## Phase 5.7 — Pre-Release Checklist

Before any public release, verify:

- [ ] All unit tests pass on CI
- [ ] PathValidator fuzz test (symlink attack) confirmed blocked
- [ ] Canary file integration test passes on Win10 + Win11
- [ ] `DryRunGuard` state machine tested: impossible to reach Execute mode without two-step confirmation
- [ ] Process blacklist covers all entries in the list (manually verify against current Windows 11 task list)
- [ ] MSIX signed with EV certificate
- [ ] ETW audit log entries present for every blocked operation
- [ ] UI tested with `narrator.exe` (screen reader) for accessibility
- [ ] RTL layout verified: no text is cut off, all scroll bars on correct side, tab order is logical
- [ ] Service pipe ACL tested: connecting as a non-admin user returns `Access Denied`
- [ ] `MoveFileEx` reboot-schedule feature tested: verify `PendingFileRenameOperations` entry is created correctly and cleaned up on next boot
