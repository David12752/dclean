using DClean.Contracts;
using DClean.Engine.Cleaning;
using NetworkFlushRequestMsg = DClean.Contracts.NetworkFlushRequest;
using DismCleanupRequestMsg  = DClean.Contracts.DismCleanupRequest;
using SetPrivacyRequestMsg   = DClean.Contracts.SetPrivacyRequest;
using DClean.Engine.Audio;
using DClean.Engine.DAW;
using DClean.Engine.Drivers;
using DClean.Engine.Network;
using DClean.Engine.OS;
using DClean.Engine.Debloat;
using DClean.Engine.Shell;
using SetContextMenuEnabledRequestMsg  = DClean.Contracts.SetContextMenuEnabledRequest;
using WinGetUpgradeRequestMsg          = DClean.Contracts.WinGetUpgradeRequest;
using DClean.Engine.Memory;
using DClean.Engine.Performance;
using ApplyTweakRequestMsg = DClean.Contracts.ApplyTweakRequest;
using RemoveAppxRequestMsg  = DClean.Contracts.RemoveAppxRequest;
using DClean.Engine.Safety;
using DClean.Engine.Storage;
using DClean.Engine.Startup;
using DClean.Engine.Uninstaller;
using PerformanceTuneRequestMsg = DClean.Contracts.PerformanceTuneRequest;
using Microsoft.Extensions.Logging;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace DClean.Service;

public sealed class PipeServer : IDisposable
{
    public const string PipeName = "DCleanService";

    private readonly FileScanner             _scanner;
    private readonly SafeDeleter             _deleter;
    private readonly BrowserCacheLocator     _cacheLocator;
    private readonly ProcessEnumerator       _procEnum;
    private readonly WorkingSetTrimmer       _trimmer;
    private readonly SafeTerminator          _terminator;
    private readonly RamMonitor              _ramMonitor;
    private readonly DryRunGuard             _dryRunGuard;
    private readonly PathValidator           _validator;
    private readonly DriverAuditor               _driverAuditor;
    private readonly DawCleaner                  _dawCleaner;
    private readonly AudioEndpointEngine         _audioEngine;
    private readonly NetworkFlusher              _netFlusher;
    private readonly ComponentStoreEngine        _compStore;
    private readonly SmartAnalyzer               _smartAnalyzer;
    private readonly WinGetUpdater               _winGet;
    private readonly ContextMenuEngine           _ctxMenu;
    private readonly Debloater                  _debloater;
    private readonly MftScanner                _mftScanner;
    private readonly PerformanceTuner          _perfTuner;
    private readonly StartupEngine            _startupEngine;
    private readonly InstalledAppDiscovery   _appDiscovery;
    private readonly LeftoverScanner         _leftoverScanner;
    private readonly SafeUninstaller         _safeUninstaller;
    private readonly ILogger<PipeServer>     _log;

    private NamedPipeServerStream? _pipe;
    private CancellationToken _ct;
    // Serialises all writes to the StreamWriter — Progress<T> fires on the thread pool
    // so without this lock concurrent callbacks corrupt the pipe stream.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public PipeServer(
        FileScanner scanner,
        SafeDeleter deleter,
        BrowserCacheLocator cacheLocator,
        ProcessEnumerator procEnum,
        WorkingSetTrimmer trimmer,
        SafeTerminator terminator,
        RamMonitor ramMonitor,
        DryRunGuard dryRunGuard,
        PathValidator validator,
        DriverAuditor driverAuditor,
        DawCleaner dawCleaner,
        AudioEndpointEngine audioEngine,
        NetworkFlusher netFlusher,
        ComponentStoreEngine compStore,
        SmartAnalyzer smartAnalyzer,
        WinGetUpdater winGet,
        ContextMenuEngine ctxMenu,
        Debloater debloater,
        MftScanner mftScanner,
        PerformanceTuner perfTuner,
        StartupEngine startupEngine,
        InstalledAppDiscovery appDiscovery,
        LeftoverScanner leftoverScanner,
        SafeUninstaller safeUninstaller,
        ILogger<PipeServer> log)
    {
        _scanner          = scanner;
        _deleter          = deleter;
        _cacheLocator     = cacheLocator;
        _procEnum         = procEnum;
        _trimmer          = trimmer;
        _terminator       = terminator;
        _ramMonitor       = ramMonitor;
        _dryRunGuard      = dryRunGuard;
        _validator        = validator;
        _driverAuditor    = driverAuditor;
        _dawCleaner       = dawCleaner;
        _audioEngine      = audioEngine;
        _netFlusher       = netFlusher;
        _compStore        = compStore;
        _smartAnalyzer    = smartAnalyzer;
        _winGet           = winGet;
        _ctxMenu          = ctxMenu;
        _debloater        = debloater;
        _mftScanner       = mftScanner;
        _perfTuner        = perfTuner;
        _startupEngine    = startupEngine;
        _appDiscovery     = appDiscovery;
        _leftoverScanner  = leftoverScanner;
        _safeUninstaller  = safeUninstaller;
        _log              = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _ct = ct;
        _log.LogInformation("PipeServer starting on \\\\.\\ pipe\\{Name}", PipeName);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _pipe = CreatePipe();
                _log.LogDebug("Waiting for UI client connection...");
                await _pipe.WaitForConnectionAsync(ct);
                _log.LogInformation("UI client connected.");
                await HandleClientAsync(_pipe, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Pipe session error — restarting listener.");
            }
            finally
            {
                _pipe?.Dispose();
                _pipe = null;
                _dryRunGuard.Reset();
            }
        }

        _log.LogInformation("PipeServer stopped.");
    }

    private static NamedPipeServerStream CreatePipe()
    {
        // Try with security ACLs first (required when running as Windows Service).
        // Falls back to a plain pipe when running non-elevated in development.
        try
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));

            return NamedPipeServerStreamAcl.Create(
                PipeName, PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Message,
                PipeOptions.Asynchronous,
                inBufferSize: 65536, outBufferSize: 65536,
                pipeSecurity: security);
        }
        catch (UnauthorizedAccessException)
        {
            // Dev / non-elevated mode: create pipe without explicit ACL.
            // The current user owns the pipe and can connect to it.
            return new NamedPipeServerStream(
                PipeName, PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Message,
                PipeOptions.Asynchronous,
                inBufferSize: 65536, outBufferSize: 65536);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
                if (line == null) break;
            }
            catch (IOException) { break; }

            PipeMessage? msg;
            try { msg = JsonSerializer.Deserialize<PipeMessage>(line); }
            catch { continue; }

            if (msg == null) continue;

            try
            {
                await DispatchAsync(msg, writer, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error handling message type {Type}", msg.Type);
                await SendAsync(writer, PipeMessageType.Error, ex.Message);
            }
        }
    }

    private async Task DispatchAsync(
        PipeMessage msg, StreamWriter writer, CancellationToken ct)
    {
        switch (msg.Type)
        {
            case PipeMessageType.ScanRequest:
            {
                var req = Deserialize<ScanRequest>(msg.JsonPayload);
                var caches = _cacheLocator.Discover();
                foreach (var c in caches) _validator.AddAllowedRoot(c.FullPath);

                string winTemp = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
                var roots = req.Targets.Count > 0
                    ? req.Targets
                    : (IEnumerable<string>)[Path.GetTempPath(), winTemp,
                        .. caches.Select(c => c.FullPath)];

                await foreach (var result in _scanner.ScanAsync(roots, ct))
                    await SendAsync(writer, PipeMessageType.ScanProgress, result);

                await SendAsync(writer, PipeMessageType.ScanComplete, "done");
                break;
            }

            case PipeMessageType.DeleteRequest:
            {
                var path    = Deserialize<string>(msg.JsonPayload);
                var outcome = _deleter.Delete(path);
                await SendAsync(writer, PipeMessageType.DeleteResult, outcome);
                break;
            }

            case PipeMessageType.ProcessListRequest:
            {
                var procs = _procEnum.GetAll();
                await SendAsync(writer, PipeMessageType.ProcessListResult, procs);
                break;
            }

            case PipeMessageType.TrimRequest:
            {
                var proc   = Deserialize<ProcessInfo>(msg.JsonPayload);
                var result = _trimmer.Trim(proc);
                await SendAsync(writer, PipeMessageType.TrimResult, result);
                break;
            }

            case PipeMessageType.TerminateRequest:
            {
                var (proc, force) = Deserialize<(ProcessInfo, bool)>(msg.JsonPayload);
                var result = await _terminator.TerminateAsync(proc, force, ct);
                await SendAsync(writer, PipeMessageType.TerminateResult, result);
                break;
            }

            case PipeMessageType.RamSnapshotRequest:
            {
                var snap = _ramMonitor.GetSnapshot();
                await SendAsync(writer, PipeMessageType.RamSnapshotResult, snap);
                break;
            }

            case PipeMessageType.RequestExecutionRequest:
            {
                string token = _dryRunGuard.RequestExecution();
                await SendAsync(writer, PipeMessageType.RequestExecutionResult, token);
                break;
            }

            case PipeMessageType.ConfirmExecutionRequest:
            {
                var token = Deserialize<string>(msg.JsonPayload);
                bool ok   = _dryRunGuard.ConfirmExecution(token);
                await SendAsync(writer, PipeMessageType.ConfirmExecutionResult, ok);
                break;
            }

            case PipeMessageType.ResetExecutionRequest:
            {
                _dryRunGuard.Reset();
                await SendAsync(writer, PipeMessageType.ResetExecutionResult, true);
                break;
            }

            // ── Phase 9G: WinGet Updater ──────────────────────────────────────

            case PipeMessageType.WinGetOutdatedRequest:
            {
                var outdated = await _winGet.GetOutdatedAsync(ct: ct);
                await SendAsync(writer, PipeMessageType.WinGetOutdatedResult, outdated);
                break;
            }

            case PipeMessageType.WinGetUpgradeRequest:
            {
                var req = Deserialize<WinGetUpgradeRequestMsg>(msg.JsonPayload);
                var source = req.UpgradeAll
                    ? _winGet.UpgradeAllAsync(ct)
                    : _winGet.UpgradeAsync(req.PackageId, ct);

                await foreach (var line in source)
                    await SendAsync(writer, PipeMessageType.WinGetUpgradeProgress, line);

                await SendAsync(writer, PipeMessageType.WinGetUpgradeResult, true);
                break;
            }

            // ── Phase 9F: Context Menu Manager ─────────────────────────

            case PipeMessageType.ContextMenuEntriesRequest:
            {
                var entries = _ctxMenu.Enumerate();
                await SendAsync(writer, PipeMessageType.ContextMenuEntriesResult, entries);
                break;
            }

            case PipeMessageType.SetContextMenuEnabledRequest:
            {
                var req = Deserialize<SetContextMenuEnabledRequestMsg>(msg.JsonPayload);
                bool ok = _ctxMenu.SetEnabled(req.Entry, req.Enable);
                await SendAsync(writer, PipeMessageType.SetContextMenuEnabledResult, ok);
                break;
            }

            case PipeMessageType.DeleteContextMenuEntryRequest:
            {
                var entry = Deserialize<ContextMenuEntry>(msg.JsonPayload);
                bool ok   = _ctxMenu.DeleteEntry(entry);
                await SendAsync(writer, PipeMessageType.DeleteContextMenuEntryResult, ok);
                break;
            }

            // ── Phase 8E: Debloater ───────────────────────────────────

            case PipeMessageType.AppxPackagesRequest:
            {
                var pkgs = _debloater.GetInstalledAppxPackages();
                await SendAsync(writer, PipeMessageType.AppxPackagesResult, pkgs);
                break;
            }

            case PipeMessageType.RemoveAppxRequest:
            {
                var req = Deserialize<RemoveAppxRequestMsg>(msg.JsonPayload);
                bool ok = _debloater.RemoveAppxPackage(req.PackageFullName);
                await SendAsync(writer, PipeMessageType.RemoveAppxResult, ok);
                break;
            }

            case PipeMessageType.TelemetryTweaksRequest:
            {
                var tweaks = _debloater.GetTelemetryTweaks();
                await SendAsync(writer, PipeMessageType.TelemetryTweaksResult, tweaks);
                break;
            }

            case PipeMessageType.ApplyTweakRequest:
            {
                var req = Deserialize<ApplyTweakRequestMsg>(msg.JsonPayload);
                bool ok = _debloater.ApplyTweak(req.Tweak, req.Enable);
                await SendAsync(writer, PipeMessageType.ApplyTweakResult, ok);
                break;
            }

            // ── Phase 8D: Large File & Duplicate Scanner ───────────────

            case PipeMessageType.MftScanRequest:
            {
                var req = Deserialize<MftScanRequest>(msg.JsonPayload);

                var progress = SafeProgress<ScanProgressInfo>(writer,
                    PipeMessageType.MftScanProgress);

                var largeFiles   = await _mftScanner.ScanLargeFilesAsync(
                    req.Roots, req.MinSizeBytes, progress, ct);

                IReadOnlyList<DuplicateGroup> groups = [];
                if (req.FindDuplicates)
                    groups = await _mftScanner.FindDuplicatesAsync(
                        req.Roots, 1024 * 1024, progress, ct);

                long total = largeFiles.Sum(f => f.SizeBytes);
                var result = new MftScanResult(largeFiles, groups, total);
                await SendAsync(writer, PipeMessageType.MftScanResult, result);
                break;
            }

            case PipeMessageType.MftDeleteRequest:
            {
                var req   = Deserialize<MftDeleteRequest>(msg.JsonPayload);
                long freed = _mftScanner.DeleteFiles(req.Paths);
                await SendAsync(writer, PipeMessageType.MftDeleteResult, freed);
                break;
            }

            // ── Phase 8C: Performance Tuner ───────────────────────────────

            case PipeMessageType.PerformanceProfileRequest:
            {
                var profile = _perfTuner.ReadCurrentProfile();
                await SendAsync(writer, PipeMessageType.PerformanceProfileResult, profile);
                break;
            }

            case PipeMessageType.PerformanceTuneRequest:
            {
                var req = Deserialize<PerformanceTuneRequestMsg>(msg.JsonPayload);
                bool ok = true;
                if (req.ApplyUltimatePlan)   ok &= _perfTuner.ApplyUltimatePlan();
                if (req.DisableUsbSuspend)   ok &= _perfTuner.SetUsbSelectiveSuspend(false);
                if (req.DisableCoreParking)  ok &= _perfTuner.SetCoreParkingDisabled(true);
                await SendAsync(writer, PipeMessageType.PerformanceTuneResult, ok);
                break;
            }

            // ── Phase 7: Startup Optimizer ────────────────────────────────

            case PipeMessageType.StartupItemsRequest:
            {
                var items = _startupEngine.Discover();
                await SendAsync(writer, PipeMessageType.StartupItemsResult, items);
                break;
            }

            case PipeMessageType.SetStartupStatusRequest:
            {
                var req = Deserialize<SetStartupStatusRequest>(msg.JsonPayload);
                bool ok = _startupEngine.SetStatus(req.Item, req.NewStatus, req.DelayMinutes);
                await SendAsync(writer, PipeMessageType.SetStartupStatusResult, ok);
                break;
            }

            // ── Phase 6: Uninstaller ─────────────────────────────────────────

            case PipeMessageType.InstalledAppsRequest:
            {
                var apps = _appDiscovery.Discover();
                await SendAsync(writer, PipeMessageType.InstalledAppsResult, apps);
                break;
            }

            case PipeMessageType.UninstallRequest:
            {
                var req      = Deserialize<UninstallRequest>(msg.JsonPayload);
                string snapDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "DClean", "Snapshots");

                _safeUninstaller.CreateRegistrySnapshot(snapDir);
                await SendAsync(writer, PipeMessageType.UninstallProgress,
                    new UninstallProgress("גיבוי ריג'יסטרי נוצר", 5));

                var zombies = _safeUninstaller.FindZombies(req.App);
                if (zombies.Count > 0)
                {
                    await SendAsync(writer, PipeMessageType.UninstallProgress,
                        new UninstallProgress($"סוגר {zombies.Count} תהליכים...", 8));
                    bool closed = await _safeUninstaller.CloseZombiesAsync(zombies, ct);
                    if (!closed)
                    {
                        await SendAsync(writer, PipeMessageType.UninstallComplete,
                            new UninstallProgress(
                                "לא כל תהליכי היישום נסגרו. מטעמי בטיחות ההסרה נעצרה. סגור את היישום ידנית ונסה שוב.",
                                100,
                                IsError: true,
                                IsCompleted: true));
                        break;
                    }
                }

                var progress = SafeProgress<UninstallProgress>(writer,
                    PipeMessageType.UninstallProgress);

                var (success, exitCode, detail) =
                    await _safeUninstaller.RunUninstallerAsync(req.App, progress, ct);

                await SendAsync(writer, PipeMessageType.UninstallComplete,
                    new UninstallProgress(detail, success ? 100 : 100,
                        IsError: !success,
                        IsCompleted: true));
                break;
            }

            case PipeMessageType.LeftoverScanRequest:
            {
                var app    = Deserialize<InstalledApp>(msg.JsonPayload);
                var result = await _leftoverScanner.ScanAsync(app, ct);
                await SendAsync(writer, PipeMessageType.LeftoverScanResult, result);
                break;
            }

            case PipeMessageType.LeftoverDeleteRequest:
            {
                var req = Deserialize<LeftoverDeleteRequest>(msg.JsonPayload);
                string snapDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "DClean", "Snapshots");

                var progress = SafeProgress<UninstallProgress>(writer,
                    PipeMessageType.UninstallProgress);

                var (deleted, freed) = await _safeUninstaller.DeleteLeftoversAsync(
                    req.ToDelete, snapDir, progress, ct);

                await SendAsync(writer, PipeMessageType.LeftoverDeleteResult,
                    new UninstallProgress($"הושלם — נמחקו {deleted} פריטים, שוחרר {FormatHelper.FormatBytes(freed)}", 100,
                        IsCompleted: true));
                break;
            }

            // ── Phase 7A: Driver Auditor ──────────────────────────────────

            case PipeMessageType.DriverAuditRequest:
            {
                var drivers = _driverAuditor.EnumerateDrivers();
                await SendAsync(writer, PipeMessageType.DriverAuditResult, drivers);
                break;
            }

            case PipeMessageType.DriverExportRequest:
            {
                string exportPath = await _driverAuditor.ExportDriversAsync(ct);
                await SendAsync(writer, PipeMessageType.DriverExportResult,
                    new DriverExportResult(!string.IsNullOrEmpty(exportPath), exportPath));
                break;
            }

            // ── Phase 9H: DAW Cleaner ─────────────────────────────────────

            case PipeMessageType.DawScanRequest:
            {
                var plugins  = _dawCleaner.ScanPlugins();
                var caches   = _dawCleaner.ScanDawCaches();
                var broken   = _dawCleaner.ScanBrokenLibraries();
                await SendAsync(writer, PipeMessageType.DawScanResult,
                    new DawScanResult(plugins, caches, broken));
                break;
            }

            case PipeMessageType.DawDeleteRequest:
            {
                var req = Deserialize<DawDeleteRequest>(msg.JsonPayload);
                var all = _dawCleaner.ScanDawCaches()
                    .Where(c => req.CachePaths.Contains(c.CachePath))
                    .ToList();
                long freed = _dawCleaner.DeleteCacheEntries(all);
                await SendAsync(writer, PipeMessageType.DawDeleteResult, freed);
                break;
            }

            // ── Phase 11I: Audio Endpoint Reset ──────────────────────────

            case PipeMessageType.AudioEndpointsRequest:
            {
                var endpoints = _audioEngine.EnumerateEndpoints();
                await SendAsync(writer, PipeMessageType.AudioEndpointsResult, endpoints);
                break;
            }

            case PipeMessageType.RestartAudioServiceRequest:
            {
                bool ok = await _audioEngine.RestartAudioServiceAsync(ct);
                await SendAsync(writer, PipeMessageType.RestartAudioServiceResult, ok);
                break;
            }

            case PipeMessageType.PrivacyEntriesRequest:
            {
                var device = Deserialize<string>(msg.JsonPayload);
                var entries = _audioEngine.GetPrivacyEntries(device);
                await SendAsync(writer, PipeMessageType.PrivacyEntriesResult, entries);
                break;
            }

            case PipeMessageType.SetPrivacyRequest:
            {
                var req = Deserialize<SetPrivacyRequestMsg>(msg.JsonPayload);
                bool ok = _audioEngine.SetPrivacyAccess(req.Device, req.AppName, req.Allow);
                await SendAsync(writer, PipeMessageType.SetPrivacyResult, ok);
                break;
            }

            // ── Phase 11J: Network Flush ──────────────────────────────────

            case PipeMessageType.NetworkFlushRequest:
            {
                var req = Deserialize<NetworkFlushRequestMsg>(msg.JsonPayload);
                await foreach (var step in _netFlusher.FlushAsync(
                    req.FlushDns, req.ReleaseRenewDhcp, req.FlushArp,
                    req.ResetWinsock, req.ResetTcpIp, ct))
                {
                    await SendAsync(writer, PipeMessageType.NetworkFlushProgress, step);
                }
                await SendAsync(writer, PipeMessageType.NetworkFlushComplete, true);
                break;
            }

            // ── Phase 12K: WinSxS + SFC ──────────────────────────────────

            case PipeMessageType.DismAnalyzeRequest:
            {
                var analysis = await _compStore.AnalyzeAsync(ct);
                await SendAsync(writer, PipeMessageType.DismAnalyzeResult, analysis);
                break;
            }

            case PipeMessageType.DismCleanupRequest:
            {
                var req = Deserialize<DismCleanupRequestMsg>(msg.JsonPayload);
                await foreach (var line in _compStore.CleanupAsync(req.ResetBase, ct))
                    await SendAsync(writer, PipeMessageType.DismCleanupProgress, line);
                await SendAsync(writer, PipeMessageType.DismCleanupComplete, true);
                break;
            }

            case PipeMessageType.SfcScanRequest:
            {
                await foreach (var line in _compStore.RunSfcAsync(ct))
                    await SendAsync(writer, PipeMessageType.SfcScanProgress, line);
                await SendAsync(writer, PipeMessageType.SfcScanComplete, true);
                break;
            }

            // ── Phase 12L: SSD S.M.A.R.T. ────────────────────────────────

            case PipeMessageType.SmartAnalyzeRequest:
            {
                var drives = await _smartAnalyzer.AnalyzeAllDrivesAsync(ct);
                await SendAsync(writer, PipeMessageType.SmartAnalyzeResult, drives);
                break;
            }

            case PipeMessageType.EnableTrimRequest:
            {
                bool ok = await _smartAnalyzer.EnableTrimAsync(ct);
                await SendAsync(writer, PipeMessageType.EnableTrimResult, ok);
                break;
            }
        }
    }

    private async Task SendAsync<T>(StreamWriter writer, PipeMessageType type, T payload)
    {
        var msg  = new PipeMessage(type, JsonSerializer.Serialize(payload));
        var line = JsonSerializer.Serialize(msg);
        await _writeLock.WaitAsync();
        try   { await writer.WriteLineAsync(line); }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Creates a Progress&lt;T&gt; whose callback marshals through _writeLock so it
    /// cannot race with other SendAsync calls on the same StreamWriter.
    /// </summary>
    private IProgress<T> SafeProgress<T>(StreamWriter writer, PipeMessageType type)
        => new Progress<T>(payload =>
        {
            // Fire-and-forget but serialised: use async Wait to avoid blocking
            // thread-pool threads, then write in the serialised section.
            _writeLock.Wait(TimeSpan.FromSeconds(5));
            try
            {
                var msg  = new PipeMessage(type, JsonSerializer.Serialize(payload));
                var line = JsonSerializer.Serialize(msg);
                writer.WriteLine(line);
                writer.Flush();
            }
            catch { /* swallow — pipe may have closed */ }
            finally { _writeLock.Release(); }
        });

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json)
            ?? throw new InvalidOperationException($"Cannot deserialize {typeof(T).Name}");

    public void Dispose() => _pipe?.Dispose();
}
