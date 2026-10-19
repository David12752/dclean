using DClean.Contracts;
using DClean.Engine.Interop;
using DClean.Engine.Logging;
using DClean.Engine.Safety;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace DClean.Engine.Cleaning;

public sealed class SafeDeleter
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation    = 33;

    private readonly PathValidator _validator;
    private readonly DryRunGuard   _dryRunGuard;
    private readonly ILogger<SafeDeleter> _log;

    public SafeDeleter(PathValidator validator, DryRunGuard dryRunGuard, ILogger<SafeDeleter> log)
    {
        _validator   = validator;
        _dryRunGuard = dryRunGuard;
        _log         = log;
    }

    public DeleteOutcome Delete(string path)
    {
        // Always re-validate even if the scanner already checked
        if (_validator.Validate(path, out var reason) == ValidationResult.Blocked)
            return new DeleteOutcome(path, DeleteStatus.Blocked, reason, 0);

        long size = 0;
        try { size = new FileInfo(path).Length; } catch { }

        if (!_dryRunGuard.IsExecuteMode)
        {
            _log.LogInformation("[DRY-RUN] Would delete: {Path} ({Size} bytes)", path, size);
            return new DeleteOutcome(path, DeleteStatus.DryRun, string.Empty, size);
        }

        try
        {
            File.Delete(path);
            DCleanEventSource.Log.FileDeleted(path, size);
            _log.LogInformation("Deleted: {Path} ({Size} bytes)", path, size);
            return new DeleteOutcome(path, DeleteStatus.Deleted, string.Empty, size);
        }
        catch (IOException ioEx) when (IsLockError(ioEx))
        {
            return ScheduleDeleteOnReboot(path, size);
        }
        catch (UnauthorizedAccessException uaEx)
        {
            _log.LogWarning("Access denied deleting {Path}: {Msg}", path, uaEx.Message);
            return new DeleteOutcome(path, DeleteStatus.AccessDenied, uaEx.Message, 0);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unexpected error deleting {Path}", path);
            return new DeleteOutcome(path, DeleteStatus.Error, ex.Message, 0);
        }
    }

    private static bool IsLockError(IOException ex)
    {
        int hr = ex.HResult & 0xFFFF;
        return hr is ErrorSharingViolation or ErrorLockViolation;
    }

    private DeleteOutcome ScheduleDeleteOnReboot(string path, long size)
    {
        // MoveFileEx with null destination = delete on next reboot.
        // Writes to HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\PendingFileRenameOperations
        bool ok = NativeMethods.MoveFileEx(path, null,
            NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);

        if (ok)
        {
            DCleanEventSource.Log.FileScheduledForRebootDeletion(path);
            _log.LogWarning("File locked — scheduled for deletion on reboot: {Path}", path);
            return new DeleteOutcome(path, DeleteStatus.ScheduledReboot, string.Empty, size);
        }

        int err = Marshal.GetLastWin32Error();
        _log.LogError("MoveFileEx reboot-schedule failed for {Path}: Win32={Error}", path, err);
        return new DeleteOutcome(path, DeleteStatus.Error, $"Win32 error {err}", 0);
    }
}
