using DClean.Contracts;
using DClean.Engine.Safety;
using Microsoft.Extensions.Logging.Abstractions;

namespace DClean.Engine.Tests;

// ── PathValidator ────────────────────────────────────────────────────────────

public class PathValidatorTests
{
    private readonly PathValidator _sut;

    public PathValidatorTests()
    {
        _sut = new PathValidator(NullLogger<PathValidator>.Instance);
        _sut.AddAllowedRoot(@"C:\Users\TestUser\AppData\Local\Temp");
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\kernel32.dll")]
    [InlineData(@"C:\Windows\SysWOW64\ntdll.dll")]
    [InlineData(@"C:\Windows\WinSxS\amd64_microsoft.windows.common-controls\comctl32.dll")]
    [InlineData(@"C:\Program Files\SomeApp\app.dll")]
    [InlineData(@"C:\pagefile.sys")]
    [InlineData(@"C:\hiberfil.sys")]
    [InlineData(@"C:\Recovery\WindowsRE\winre.wim")]
    [InlineData(@"C:\$Recycle.Bin\SomeFile.tmp")]
    public void Validate_ForbiddenPaths_ReturnsBlocked(string path)
    {
        var result = _sut.Validate(path, out var reason);
        Assert.Equal(ValidationResult.Blocked, result);
        Assert.NotEmpty(reason);
    }

    [Theory]
    [InlineData(@"C:\Users\TestUser\AppData\Local\Temp\setup.exe")]
    [InlineData(@"C:\Users\TestUser\AppData\Local\Temp\helper.dll")]
    [InlineData(@"C:\Users\TestUser\AppData\Local\Temp\driver.sys")]
    [InlineData(@"C:\Users\TestUser\AppData\Local\Temp\shortcut.lnk")]
    public void Validate_ForbiddenExtensionInAllowedRoot_ReturnsBlocked(string path)
    {
        var result = _sut.Validate(path, out _);
        Assert.Equal(ValidationResult.Blocked, result);
    }

    [Theory]
    [InlineData(@"C:\Users\TestUser\AppData\Local\Temp\abc123.tmp")]
    [InlineData(@"C:\Users\TestUser\AppData\Local\Temp\cache.dat")]
    [InlineData(@"C:\Users\TestUser\AppData\Local\Temp\subfolder\log.txt")]
    public void Validate_AllowedPaths_ReturnsAllowed(string path)
    {
        var result = _sut.Validate(path, out _);
        Assert.Equal(ValidationResult.Allowed, result);
    }

    [Fact]
    public void Validate_PathNotInAllowedRoot_ReturnsBlocked()
    {
        var result = _sut.Validate(@"C:\Users\TestUser\Documents\report.tmp", out var reason);
        Assert.Equal(ValidationResult.Blocked, result);
        Assert.Contains("allowed root", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"\\server\share\file.tmp")]
    [InlineData(@"//server/share/file.tmp")]
    public void Validate_UncPaths_ReturnsBlocked(string path)
    {
        var result = _sut.Validate(path, out _);
        Assert.Equal(ValidationResult.Blocked, result);
    }

    [Fact]
    public void Validate_EmptyPath_ReturnsBlocked()
    {
        var result = _sut.Validate("", out var reason);
        Assert.Equal(ValidationResult.Blocked, result);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Validate_ForbiddenFilename_ReturnsBlocked()
    {
        _sut.Validate(@"C:\Users\TestUser\AppData\Local\Temp\pagefile.sys", out _);
        var result = _sut.Validate(@"C:\Users\TestUser\AppData\Local\Temp\bootmgr", out var reason);
        Assert.Equal(ValidationResult.Blocked, result);
        Assert.NotEmpty(reason);
    }
}

// ── DryRunGuard ──────────────────────────────────────────────────────────────

public class DryRunGuardTests
{
    [Fact]
    public void DefaultMode_IsDryRun()
    {
        var guard = new DryRunGuard(NullLogger<DryRunGuard>.Instance);
        Assert.False(guard.IsExecuteMode);
        Assert.Equal(ExecutionMode.DryRun, guard.CurrentMode);
    }

    [Fact]
    public void ConfirmExecution_WithCorrectToken_SetsExecuteMode()
    {
        var guard = new DryRunGuard(NullLogger<DryRunGuard>.Instance);
        var token = guard.RequestExecution();

        Assert.Equal(ExecutionMode.AwaitingConfirmation, guard.CurrentMode);

        bool ok = guard.ConfirmExecution(token);
        Assert.True(ok);
        Assert.True(guard.IsExecuteMode);
    }

    [Fact]
    public void ConfirmExecution_WithWrongToken_RemainsBlocked()
    {
        var guard = new DryRunGuard(NullLogger<DryRunGuard>.Instance);
        guard.RequestExecution();

        bool ok = guard.ConfirmExecution("wrong-token-000");
        Assert.False(ok);
        Assert.False(guard.IsExecuteMode);
    }

    [Fact]
    public void Reset_AfterExecution_ReturnsToDryRun()
    {
        var guard = new DryRunGuard(NullLogger<DryRunGuard>.Instance);
        var token = guard.RequestExecution();
        guard.ConfirmExecution(token);
        Assert.True(guard.IsExecuteMode);

        guard.Reset();
        Assert.False(guard.IsExecuteMode);
        Assert.Equal(ExecutionMode.DryRun, guard.CurrentMode);
    }

    [Fact]
    public void ConfirmExecution_WithoutRequestFirst_ReturnsFalse()
    {
        var guard = new DryRunGuard(NullLogger<DryRunGuard>.Instance);
        bool ok = guard.ConfirmExecution(Guid.NewGuid().ToString("N"));
        Assert.False(ok);
        Assert.False(guard.IsExecuteMode);
    }

    [Fact]
    public void Token_IsUnique_EachRequest()
    {
        var guard  = new DryRunGuard(NullLogger<DryRunGuard>.Instance);
        var token1 = guard.RequestExecution();
        guard.Reset();
        var token2 = guard.RequestExecution();
        Assert.NotEqual(token1, token2);
    }
}

// ── ProcessBlacklist ─────────────────────────────────────────────────────────

public class ProcessBlacklistTests
{
    [Theory]
    [InlineData("csrss.exe")]
    [InlineData("smss.exe")]
    [InlineData("lsass.exe")]
    [InlineData("services.exe")]
    [InlineData("winlogon.exe")]
    [InlineData("wininit.exe")]
    [InlineData("svchost.exe")]
    [InlineData("dwm.exe")]
    [InlineData("System")]
    [InlineData("DClean.Service.exe")]
    [InlineData("DClean.UI.exe")]
    public void IsCritical_KnownCriticalProcesses_ReturnsTrue(string name)
    {
        Assert.True(ProcessBlacklist.IsCritical(name));
    }

    [Theory]
    [InlineData("CSRSS.EXE")]
    [InlineData("Lsass.Exe")]
    [InlineData("SVCHOST.EXE")]
    public void IsCritical_CaseInsensitive(string name)
    {
        Assert.True(ProcessBlacklist.IsCritical(name));
    }

    [Theory]
    [InlineData("notepad.exe")]
    [InlineData("chrome.exe")]
    [InlineData("code.exe")]
    [InlineData("vlc.exe")]
    public void IsCritical_NonCriticalProcesses_ReturnsFalse(string name)
    {
        Assert.False(ProcessBlacklist.IsCritical(name));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\csrss.exe")]
    [InlineData(@"C:\Windows\System32\lsass.exe")]
    public void IsCritical_FullPath_ReturnsTrue(string fullPath)
    {
        Assert.True(ProcessBlacklist.IsCritical(fullPath));
    }

    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("sihost.exe")]
    [InlineData("ShellExperienceHost.exe")]
    [InlineData("StartMenuExperienceHost.exe")]
    public void IsCritical_ShellProcesses_ReturnsTrue(string name)
    {
        Assert.True(ProcessBlacklist.IsCritical(name));
    }

    [Theory]
    [InlineData("audiodg.exe")]
    [InlineData("WerFault.exe")]
    [InlineData("spoolsv.exe")]
    public void IsProtected_KnownProtectedProcesses_ReturnsTrue(string name)
    {
        Assert.True(ProcessBlacklist.IsProtected(name));
    }
}

// ── SafeTerminator ────────────────────────────────────────────────────────────

public class SafeTerminatorTests
{
    [Theory]
    [InlineData("csrss.exe")]
    [InlineData("smss.exe")]
    [InlineData("lsass.exe")]
    [InlineData("services.exe")]
    [InlineData("winlogon.exe")]
    [InlineData("System")]
    [InlineData("explorer.exe")]
    [InlineData("svchost.exe")]
    public async Task Terminate_CriticalProcess_IsAlwaysBlocked(string processName)
    {
        var guard      = new DryRunGuard(NullLogger<DryRunGuard>.Instance);
        var terminator = new DClean.Engine.Memory.SafeTerminator(
            guard, NullLogger<DClean.Engine.Memory.SafeTerminator>.Instance);

        var proc = new DClean.Contracts.ProcessInfo(
            Pid:             9999,
            Name:            processName,
            WorkingSetBytes: 0,
            PrivateBytesKb:  0,
            IsCritical:      ProcessBlacklist.IsCritical(processName),
            IsProtected:     ProcessBlacklist.IsProtected(processName));

        var result = await terminator.TerminateAsync(proc, forceIfNeeded: true);
        Assert.False(result.Success);
        Assert.Contains("Blocked", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("audiodg.exe")]
    [InlineData("WerFault.exe")]
    [InlineData("spoolsv.exe")]
    public async Task Terminate_ProtectedProcess_IsAlwaysBlocked(string processName)
    {
        var guard      = new DryRunGuard(NullLogger<DryRunGuard>.Instance);
        var terminator = new DClean.Engine.Memory.SafeTerminator(
            guard, NullLogger<DClean.Engine.Memory.SafeTerminator>.Instance);

        var proc = new DClean.Contracts.ProcessInfo(
            Pid:             9999,
            Name:            processName,
            WorkingSetBytes: 0,
            PrivateBytesKb:  0,
            IsCritical:      ProcessBlacklist.IsCritical(processName),
            IsProtected:     ProcessBlacklist.IsProtected(processName));

        var result = await terminator.TerminateAsync(proc, forceIfNeeded: true);
        Assert.False(result.Success);
        Assert.Contains("Blocked", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Terminate_InDryRunMode_ReturnsDryRun()
    {
        var guard      = new DryRunGuard(NullLogger<DryRunGuard>.Instance);
        var terminator = new DClean.Engine.Memory.SafeTerminator(
            guard, NullLogger<DClean.Engine.Memory.SafeTerminator>.Instance);

        var proc = new DClean.Contracts.ProcessInfo(
            Pid:             99999,
            Name:            "notepad.exe",
            WorkingSetBytes: 0,
            PrivateBytesKb:  0,
            IsCritical:      false,
            IsProtected:     false);

        // Guard is in DryRun by default
        var result = await terminator.TerminateAsync(proc, forceIfNeeded: true);
        Assert.True(result.Success);
        Assert.Equal("DryRun", result.Detail);
    }
}

