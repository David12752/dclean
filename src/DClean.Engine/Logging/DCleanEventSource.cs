using System.Diagnostics.Tracing;

namespace DClean.Engine.Logging;

[EventSource(Name = "DClean-Engine")]
internal sealed class DCleanEventSource : EventSource
{
    public static readonly DCleanEventSource Log = new();

    [Event(1, Level = EventLevel.Informational, Keywords = Keywords.FileOps)]
    public void FileScanned(string path, long sizeBytes) =>
        WriteEvent(1, path, sizeBytes);

    [Event(2, Level = EventLevel.Warning, Keywords = Keywords.FileOps)]
    public void FileSkipped(string path, string reason) =>
        WriteEvent(2, path, reason);

    [Event(3, Level = EventLevel.Critical, Keywords = Keywords.Safety)]
    public void SafetyGateBlocked(string path, string rule) =>
        WriteEvent(3, path, rule);

    [Event(4, Level = EventLevel.Informational, Keywords = Keywords.FileOps)]
    public void FileDeleted(string path, long bytesFreed) =>
        WriteEvent(4, path, bytesFreed);

    [Event(5, Level = EventLevel.Warning, Keywords = Keywords.FileOps)]
    public void FileScheduledForRebootDeletion(string path) =>
        WriteEvent(5, path);

    [Event(6, Level = EventLevel.Informational, Keywords = Keywords.Process)]
    public void ProcessTerminated(int pid, string name, bool wasGraceful) =>
        WriteEvent(6, pid, name, wasGraceful);

    [Event(7, Level = EventLevel.Warning, Keywords = Keywords.Process)]
    public void ProcessTerminationBlocked(int pid, string name, string reason) =>
        WriteEvent(7, pid, name, reason);

    [Event(8, Level = EventLevel.Informational, Keywords = Keywords.Process)]
    public void WorkingSetTrimmed(int pid, string name) =>
        WriteEvent(8, pid, name);

    public static class Keywords
    {
        public const EventKeywords FileOps = (EventKeywords)0x1;
        public const EventKeywords Safety  = (EventKeywords)0x2;
        public const EventKeywords Process = (EventKeywords)0x4;
    }
}
