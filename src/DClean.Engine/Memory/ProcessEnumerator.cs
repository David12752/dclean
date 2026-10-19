using DClean.Contracts;
using DClean.Engine.Safety;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace DClean.Engine.Memory;

public sealed class ProcessEnumerator
{
    private readonly ILogger<ProcessEnumerator> _log;

    public ProcessEnumerator(ILogger<ProcessEnumerator> log) => _log = log;

    public IReadOnlyList<ProcessInfo> GetAll()
    {
        var results = new List<ProcessInfo>();

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                string name = proc.ProcessName + ".exe";

                long workingSet  = 0;
                long privateKb   = 0;
                try
                {
                    workingSet = proc.WorkingSet64;
                    privateKb  = proc.PrivateMemorySize64 / 1024;
                }
                catch { /* Access denied on some system processes — non-fatal */ }

                results.Add(new ProcessInfo(
                    Pid:             proc.Id,
                    Name:            name,
                    WorkingSetBytes: workingSet,
                    PrivateBytesKb:  privateKb,
                    IsCritical:      ProcessBlacklist.IsCritical(name),
                    IsProtected:     ProcessBlacklist.IsProtected(name)));
            }
            catch (Exception ex)
            {
                _log.LogDebug("Skipping PID {Pid}: {Msg}", proc.Id, ex.Message);
            }
            finally
            {
                proc.Dispose();
            }
        }

        return [.. results.OrderByDescending(p => p.WorkingSetBytes)];
    }
}
