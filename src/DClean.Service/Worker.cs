namespace DClean.Service;

public sealed class Worker : BackgroundService
{
    private readonly PipeServer _pipeServer;
    private readonly ILogger<Worker> _log;

    public Worker(PipeServer pipeServer, ILogger<Worker> log)
    {
        _pipeServer = pipeServer;
        _log        = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("DClean Service started.");
        try
        {
            await _pipeServer.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "PipeServer crashed unexpectedly.");
            throw;
        }
        _log.LogInformation("DClean Service stopped.");
    }
}
