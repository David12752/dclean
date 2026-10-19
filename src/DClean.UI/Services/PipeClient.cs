using DClean.Contracts;
using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DClean.UI.Services;

public sealed class PipeClient : IPipeClient
{
    private const string PipeName = "DCleanService";

    private NamedPipeClientStream? _pipe;
    private StreamReader?          _reader;
    private StreamWriter?          _writer;
    // One slot — every public method acquires this for the full send + receive round-trip.
    // The pipe is inherently serial; concurrent callers queue up here instead of racing.
    private readonly SemaphoreSlim _pipeLock = new(1, 1);

    public async Task<bool> EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (IsConnected)
            return true;

        for (int i = 0; i < 3; i++)
        {
            try
            {
                await ConnectAsync(TimeSpan.FromSeconds(3), ct);
                return true;
            }
            catch when (!ct.IsCancellationRequested)
            {
                if (!ServiceLauncher.IsPipeAvailable())
                    break;

                await Task.Delay(1000, ct);
            }
        }

        string? found = await ServiceLauncher.EnsureRunningAsync();
        if (found == null)
            return false;

        for (int i = 0; i < 8; i++)
        {
            try
            {
                await ConnectAsync(TimeSpan.FromSeconds(3), ct);
                return true;
            }
            catch when (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
            }
        }

        return false;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
        => await ConnectAsync(TimeSpan.FromSeconds(5), ct);

    public async Task ConnectAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        // Clean up any previous (failed or stale) connection
        _reader?.Dispose(); _reader = null;
        _writer?.Dispose(); _writer = null;
        _pipe?.Dispose();   _pipe   = null;

        var pipe = new NamedPipeClientStream(".", PipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        await pipe.ConnectAsync(cts.Token);   // throws on timeout — pipe is not assigned yet
        _pipe   = pipe;
        _reader = new StreamReader(_pipe,  leaveOpen: true);
        _writer = new StreamWriter(_pipe, leaveOpen: true) { AutoFlush = true };
    }

    public bool IsConnected => _pipe?.IsConnected == true;

    public async IAsyncEnumerable<ScanResult> ScanAsync(
        bool dryRun,
        IReadOnlyList<string>? targets = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var req = new ScanRequest(
            dryRun ? OperationMode.DryRun : OperationMode.Execute,
            targets ?? []);

        var msgs = await ExecuteStreamAsync(PipeMessageType.ScanRequest, req,
            PipeMessageType.ScanComplete, TimeSpan.FromMinutes(10), ct);
        foreach (var msg in msgs)
        {
            var result = Deserialize<ScanResult>(msg.JsonPayload);
            if (result != null) yield return result;
        }
    }

    public async Task<DeleteOutcome?> DeleteAsync(string path, CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.DeleteRequest, path, ct);
        return msg != null ? Deserialize<DeleteOutcome>(msg.JsonPayload) : null;
    }

    public async Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.ProcessListRequest, string.Empty, ct);
        return msg != null ? Deserialize<List<ProcessInfo>>(msg.JsonPayload) ?? [] : [];
    }

    public async Task<TrimResult?> TrimAsync(ProcessInfo proc, CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.TrimRequest, proc, ct);
        return msg != null ? Deserialize<TrimResult>(msg.JsonPayload) : null;
    }

    public async Task<TerminateResult?> TerminateAsync(
        ProcessInfo proc, bool force, CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.TerminateRequest, (proc, force), ct);
        return msg != null ? Deserialize<TerminateResult>(msg.JsonPayload) : null;
    }

    public async Task<RamSnapshot?> GetRamSnapshotAsync(CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.RamSnapshotRequest, string.Empty,
            TimeSpan.FromSeconds(10), ct);
        return msg != null ? Deserialize<RamSnapshot>(msg.JsonPayload) : null;
    }

    public async Task<string?> RequestExecutionAsync(CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.RequestExecutionRequest, string.Empty, ct);
        return msg != null ? Deserialize<string>(msg.JsonPayload) : null;
    }

    public async Task<bool> ConfirmExecutionAsync(string token, CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.ConfirmExecutionRequest, token, ct);
        return msg != null && Deserialize<bool>(msg.JsonPayload);
    }

    public async Task<bool> ResetExecutionAsync(CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.ResetExecutionRequest, string.Empty, ct);
        return msg != null && Deserialize<bool>(msg.JsonPayload);
    }

    // ── Phase 9G: WinGet Updater ──────────────────────────────────────────────

    public async Task<IReadOnlyList<WinGetPackage>> GetOutdatedPackagesAsync(
        CancellationToken ct = default)
    {
        // WinGet can be very slow — allow up to 90 seconds
        var msg = await ExecuteAsync(PipeMessageType.WinGetOutdatedRequest, string.Empty,
            TimeSpan.FromSeconds(90), ct);
        return msg != null ? Deserialize<List<WinGetPackage>>(msg.JsonPayload) ?? [] : [];
    }

    public async IAsyncEnumerable<string> UpgradePackageAsync(
        string packageId,
        bool upgradeAll,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var msgs = await ExecuteStreamAsync(PipeMessageType.WinGetUpgradeRequest,
            new WinGetUpgradeRequest(packageId, upgradeAll),
            PipeMessageType.WinGetUpgradeResult, TimeSpan.FromMinutes(15), ct);
        foreach (var msg in msgs)
        {
            if (msg.Type == PipeMessageType.WinGetUpgradeProgress)
            {
                var line = Deserialize<string>(msg.JsonPayload);
                if (line != null) yield return line;
            }
        }
    }

    // ── Phase 9F: Context Menu Manager ───────────────────────────────────────

    public async Task<IReadOnlyList<ContextMenuEntry>> GetContextMenuEntriesAsync(
        CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.ContextMenuEntriesRequest, string.Empty, ct);
        return msg != null ? Deserialize<List<ContextMenuEntry>>(msg.JsonPayload) ?? [] : [];
    }

    public async Task<bool> SetContextMenuEnabledAsync(
        ContextMenuEntry entry, bool enable, CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.SetContextMenuEnabledRequest,
            new SetContextMenuEnabledRequest(entry, enable), ct);
        return msg != null && Deserialize<bool>(msg.JsonPayload);
    }

    public async Task<bool> DeleteContextMenuEntryAsync(
        ContextMenuEntry entry, CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.DeleteContextMenuEntryRequest, entry, ct);
        return msg != null && Deserialize<bool>(msg.JsonPayload);
    }

    // ── Phase 8E: Debloater ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<AppxPackageEntry>> GetAppxPackagesAsync(
        CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.AppxPackagesRequest, string.Empty, ct);
        return msg != null ? Deserialize<List<AppxPackageEntry>>(msg.JsonPayload) ?? [] : [];
    }

    public async Task<bool> RemoveAppxPackageAsync(
        string packageFullName, CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.RemoveAppxRequest,
            new RemoveAppxRequest(packageFullName), ct);
        return msg != null && Deserialize<bool>(msg.JsonPayload);
    }

    public async Task<IReadOnlyList<TelemetryTweak>> GetTelemetryTweaksAsync(
        CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.TelemetryTweaksRequest, string.Empty, ct);
        return msg != null ? Deserialize<List<TelemetryTweak>>(msg.JsonPayload) ?? [] : [];
    }

    public async Task<bool> ApplyTweakAsync(
        TelemetryTweak tweak, bool enable, CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.ApplyTweakRequest,
            new ApplyTweakRequest(tweak, enable), ct);
        return msg != null && Deserialize<bool>(msg.JsonPayload);
    }

    // ── Phase 8D: Large File & Duplicate Scanner ─────────────────────────────

    public async IAsyncEnumerable<object> ScanLargeFilesAsync(
        MftScanRequest req,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var msgs = await ExecuteStreamIncludingTerminalAsync(PipeMessageType.MftScanRequest, req,
            PipeMessageType.MftScanResult, TimeSpan.FromMinutes(30), ct);
        foreach (var msg in msgs)
        {
            if (msg.Type == PipeMessageType.MftScanProgress)
            {
                var p = Deserialize<ScanProgressInfo>(msg.JsonPayload);
                if (p != null) yield return p;
            }
            else if (msg.Type == PipeMessageType.MftScanResult)
            {
                var r = Deserialize<MftScanResult>(msg.JsonPayload);
                if (r != null) yield return r;
            }
        }
    }

    public async Task<long> DeleteMftFilesAsync(
        IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.MftDeleteRequest, new MftDeleteRequest(paths), ct);
        return msg != null ? Deserialize<long>(msg.JsonPayload) : 0L;
    }

    // ── Phase 8C: Performance Tuner ──────────────────────────────────────────

    public async Task<PerformanceProfile?> GetPerformanceProfileAsync(CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.PerformanceProfileRequest, string.Empty, ct);
        return msg != null ? Deserialize<PerformanceProfile>(msg.JsonPayload) : null;
    }

    public async Task<bool> ApplyPerformanceTuneAsync(
        PerformanceTuneRequest req, CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.PerformanceTuneRequest, req, ct);
        return msg != null && Deserialize<bool>(msg.JsonPayload);
    }

    // ── Phase 7: Startup Optimizer ───────────────────────────────────────────

    public async Task<IReadOnlyList<StartupItem>> GetStartupItemsAsync(
        CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.StartupItemsRequest, string.Empty, ct);
        return msg != null ? Deserialize<List<StartupItem>>(msg.JsonPayload) ?? [] : [];
    }

    public async Task<bool> SetStartupStatusAsync(
        StartupItem item, StartupItemStatus newStatus, int delayMinutes = 0,
        CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.SetStartupStatusRequest,
            new SetStartupStatusRequest(item, newStatus, delayMinutes), ct);
        return msg != null && Deserialize<bool>(msg.JsonPayload);
    }

    // ── Phase 6: Uninstaller ─────────────────────────────────────────────────

    public async Task<IReadOnlyList<InstalledApp>> GetInstalledAppsAsync(
        CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.InstalledAppsRequest, string.Empty,
            TimeSpan.FromSeconds(60), ct);
        return msg != null ? Deserialize<List<InstalledApp>>(msg.JsonPayload) ?? [] : [];
    }

    public async IAsyncEnumerable<UninstallProgress> UninstallAsync(
        InstalledApp app,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var msgs = await ExecuteStreamAsync(PipeMessageType.UninstallRequest,
            new UninstallRequest(app, ScanLeftovers: false),
            PipeMessageType.UninstallComplete, TimeSpan.FromMinutes(10), ct);
        foreach (var msg in msgs)
        {
            var p = Deserialize<UninstallProgress>(msg.JsonPayload);
            if (p != null) yield return p;
        }
    }

    public async Task<LeftoverScanResult?> ScanLeftoversAsync(
        InstalledApp app, CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.LeftoverScanRequest, app,
            TimeSpan.FromSeconds(60), ct);
        return msg != null ? Deserialize<LeftoverScanResult>(msg.JsonPayload) : null;
    }

    public async IAsyncEnumerable<UninstallProgress> DeleteLeftoversAsync(
        IReadOnlyList<LeftoverCandidate> candidates,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var msgs = await ExecuteStreamAsync(PipeMessageType.LeftoverDeleteRequest,
            new LeftoverDeleteRequest(candidates),
            PipeMessageType.LeftoverDeleteResult, TimeSpan.FromMinutes(5), ct);
        foreach (var msg in msgs)
        {
            var p = Deserialize<UninstallProgress>(msg.JsonPayload);
            if (p != null) yield return p;
        }
    }

    // ── Phase 7A: Driver Auditor ──────────────────────────────────────────────

    public async Task<IReadOnlyList<DriverInfo>> GetDriversAsync(CancellationToken ct = default)
    {
        // WMI enumeration can take 10-30s
        var msg = await ExecuteAsync(PipeMessageType.DriverAuditRequest, string.Empty,
            TimeSpan.FromSeconds(60), ct);
        return msg != null ? Deserialize<List<DriverInfo>>(msg.JsonPayload) ?? [] : [];
    }

    public async Task<DriverExportResult?> ExportDriversAsync(CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.DriverExportRequest, string.Empty,
            TimeSpan.FromMinutes(5), ct);
        return msg != null ? Deserialize<DriverExportResult>(msg.JsonPayload) : null;
    }

    // ── Phase 9H: DAW Cleaner ────────────────────────────────────────────────

    public async Task<DawScanResult?> ScanDawAsync(CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.DawScanRequest, string.Empty, ct);
        return msg != null ? Deserialize<DawScanResult>(msg.JsonPayload) : null;
    }

    public async Task<long> DeleteDawCachesAsync(
        IReadOnlyList<string> cachePaths, CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.DawDeleteRequest, new DawDeleteRequest(cachePaths), ct);
        return msg != null ? Deserialize<long>(msg.JsonPayload) : 0L;
    }

    // ── Phase 11I: Audio Endpoint Reset ──────────────────────────────────────

    public async Task<IReadOnlyList<AudioEndpointInfo>> GetAudioEndpointsAsync(
        CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.AudioEndpointsRequest, string.Empty, ct);
        return msg != null ? Deserialize<List<AudioEndpointInfo>>(msg.JsonPayload) ?? [] : [];
    }

    public async Task<bool> RestartAudioServiceAsync(CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.RestartAudioServiceRequest, string.Empty, ct);
        return msg != null && Deserialize<bool>(msg.JsonPayload);
    }

    public async Task<IReadOnlyList<PrivacyAppEntry>> GetPrivacyEntriesAsync(
        string device, CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.PrivacyEntriesRequest, device, ct);
        return msg != null ? Deserialize<List<PrivacyAppEntry>>(msg.JsonPayload) ?? [] : [];
    }

    public async Task<bool> SetPrivacyAccessAsync(
        string device, string appName, bool allow, CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.SetPrivacyRequest,
            new SetPrivacyRequest(device, appName, allow), ct);
        return msg != null && Deserialize<bool>(msg.JsonPayload);
    }

    // ── Phase 11J: Network Flush ─────────────────────────────────────────────

    public async IAsyncEnumerable<NetworkFlushStep> FlushNetworkAsync(
        NetworkFlushRequest req,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var msgs = await ExecuteStreamAsync(PipeMessageType.NetworkFlushRequest, req,
            PipeMessageType.NetworkFlushComplete, TimeSpan.FromMinutes(5), ct);
        foreach (var msg in msgs)
        {
            var step = Deserialize<NetworkFlushStep>(msg.JsonPayload);
            if (step != null) yield return step;
        }
    }

    // ── Phase 12K: WinSxS + SFC ─────────────────────────────────────────────

    public async Task<ComponentStoreAnalysis?> AnalyzeComponentStoreAsync(
        CancellationToken ct = default)
    {
        // DISM AnalyzeComponentStore can take several minutes
        var msg = await ExecuteAsync(PipeMessageType.DismAnalyzeRequest, string.Empty,
            TimeSpan.FromMinutes(15), ct);
        return msg != null ? Deserialize<ComponentStoreAnalysis>(msg.JsonPayload) : null;
    }

    public async IAsyncEnumerable<string> RunDismCleanupAsync(
        bool resetBase,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var msgs = await ExecuteStreamAsync(PipeMessageType.DismCleanupRequest,
            new DismCleanupRequest(resetBase),
            PipeMessageType.DismCleanupComplete, TimeSpan.FromMinutes(30), ct);
        foreach (var msg in msgs)
        {
            var line = Deserialize<string>(msg.JsonPayload);
            if (line != null) yield return line;
        }
    }

    public async IAsyncEnumerable<string> RunSfcAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var msgs = await ExecuteStreamAsync(PipeMessageType.SfcScanRequest, string.Empty,
            PipeMessageType.SfcScanComplete, TimeSpan.FromMinutes(30), ct);
        foreach (var msg in msgs)
        {
            var line = Deserialize<string>(msg.JsonPayload);
            if (line != null) yield return line;
        }
    }

    // ── Phase 12L: SSD S.M.A.R.T. ────────────────────────────────────────────

    public async Task<IReadOnlyList<DriveHealth>> GetDriveHealthAsync(
        CancellationToken ct = default)
    {
        // PowerShell + WMI — allow up to 60 seconds
        var msg = await ExecuteAsync(PipeMessageType.SmartAnalyzeRequest, string.Empty,
            TimeSpan.FromSeconds(60), ct);
        return msg != null ? Deserialize<List<DriveHealth>>(msg.JsonPayload) ?? [] : [];
    }

    public async Task<bool> EnableTrimAsync(CancellationToken ct = default)
    {
        var msg = await ExecuteAsync(PipeMessageType.EnableTrimRequest, string.Empty, ct);
        return msg != null && Deserialize<bool>(msg.JsonPayload);
    }

    // ── Core thread-safe helpers ─────────────────────────────────────────────

    /// <summary>
    /// Sends one request and receives exactly one response, holding the pipe lock
    /// for the entire round-trip so concurrent callers queue rather than interleave.
    /// </summary>
    private async Task<PipeMessage?> ExecuteAsync<T>(
        PipeMessageType type, T payload,
        TimeSpan timeout, CancellationToken ct)
    {
        if (_writer == null || _reader == null)
            throw new InvalidOperationException("Not connected.");

        // Per-request timeout (on top of any caller-supplied ct)
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        await _pipeLock.WaitAsync(cts.Token);
        try
        {
            await WriteLineAsync(type, payload, cts.Token);
            var line = await _reader.ReadLineAsync(cts.Token);
            return line == null ? null : JsonSerializer.Deserialize<PipeMessage>(line);
        }
        finally { _pipeLock.Release(); }
    }

    private Task<PipeMessage?> ExecuteAsync<T>(PipeMessageType type, T payload, CancellationToken ct)
        => ExecuteAsync(type, payload, TimeSpan.FromSeconds(30), ct);

    /// <summary>
    /// Sends one request and collects all streaming responses until the terminal
    /// message type, holding the pipe lock for the entire exchange.
    /// </summary>
    private async Task<List<PipeMessage>> ExecuteStreamAsync<T>(
        PipeMessageType type, T payload, PipeMessageType terminal,
        TimeSpan timeout, CancellationToken ct)
    {
        if (_writer == null || _reader == null)
            throw new InvalidOperationException("Not connected.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        await _pipeLock.WaitAsync(cts.Token);
        var results = new List<PipeMessage>();
        try
        {
            await WriteLineAsync(type, payload, cts.Token);
            while (!cts.Token.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(cts.Token);
                if (line == null) break;
                var msg = JsonSerializer.Deserialize<PipeMessage>(line);
                if (msg == null) break;
                if (msg.Type == terminal) break;
                results.Add(msg);
            }
        }
        finally { _pipeLock.Release(); }
        return results;
    }

    private async Task<List<PipeMessage>> ExecuteStreamIncludingTerminalAsync<T>(
        PipeMessageType type, T payload, PipeMessageType terminal,
        TimeSpan timeout, CancellationToken ct)
    {
        if (_writer == null || _reader == null)
            throw new InvalidOperationException("Not connected.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        await _pipeLock.WaitAsync(cts.Token);
        var results = new List<PipeMessage>();
        try
        {
            await WriteLineAsync(type, payload, cts.Token);
            while (!cts.Token.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(cts.Token);
                if (line == null) break;
                var msg = JsonSerializer.Deserialize<PipeMessage>(line);
                if (msg == null) break;
                results.Add(msg);
                if (msg.Type == terminal) break;
            }
        }
        finally { _pipeLock.Release(); }
        return results;
    }

    private Task<List<PipeMessage>> ExecuteStreamAsync<T>(
        PipeMessageType type, T payload, PipeMessageType terminal, CancellationToken ct)
        => ExecuteStreamAsync(type, payload, terminal, TimeSpan.FromMinutes(5), ct);

    private async Task WriteLineAsync<T>(PipeMessageType type, T payload, CancellationToken ct)
    {
        var msg  = new PipeMessage(type, JsonSerializer.Serialize(payload));
        var line = JsonSerializer.Serialize(msg);
        await _writer!.WriteLineAsync(line.AsMemory(), ct);
    }

    private static T? Deserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json); }
        catch { return default; }
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _pipe?.Dispose();
        _pipeLock.Dispose();
    }
}
