using Microsoft.Extensions.Logging;

namespace DClean.Engine.Safety;

public enum ExecutionMode { DryRun, AwaitingConfirmation, Execute }

public sealed class DryRunGuard
{
    private readonly object _lock = new();
    private ExecutionMode _mode = ExecutionMode.DryRun;
    private string? _confirmationToken;
    private readonly ILogger<DryRunGuard> _log;

    public DryRunGuard(ILogger<DryRunGuard> log) => _log = log;

    public ExecutionMode CurrentMode { get { lock (_lock) return _mode; } }
    public bool IsExecuteMode { get { lock (_lock) return _mode == ExecutionMode.Execute; } }

    public string RequestExecution()
    {
        lock (_lock)
        {
            _mode = ExecutionMode.AwaitingConfirmation;
            _confirmationToken = Guid.NewGuid().ToString("N");
            _log.LogWarning("Execution requested. Confirmation token issued: {Token}", _confirmationToken);
            return _confirmationToken;
        }
    }

    public bool ConfirmExecution(string token)
    {
        lock (_lock)
        {
            if (_mode != ExecutionMode.AwaitingConfirmation ||
                !string.Equals(token, _confirmationToken, StringComparison.Ordinal))
            {
                _log.LogError("Execution confirmation FAILED. Resetting to DryRun.");
                ResetCore();
                return false;
            }
            _mode = ExecutionMode.Execute;
            _confirmationToken = null;
            _log.LogWarning("Execution CONFIRMED. Mode is now Execute.");
            return true;
        }
    }

    public void Reset()
    {
        lock (_lock) ResetCore();
    }

    private void ResetCore()
    {
        _mode = ExecutionMode.DryRun;
        _confirmationToken = null;
    }
}
