using DClean.Contracts;
using System.Runtime.CompilerServices;

namespace DClean.UI.Services;

public interface IPipeClient : IDisposable
{
    bool IsConnected { get; }

    Task<bool> EnsureConnectedAsync(CancellationToken ct = default);
    Task ConnectAsync(CancellationToken ct = default);
    Task ConnectAsync(TimeSpan timeout, CancellationToken ct = default);

    IAsyncEnumerable<ScanResult> ScanAsync(
        bool dryRun,
        IReadOnlyList<string>? targets = null,
        CancellationToken ct = default);

    Task<DeleteOutcome?> DeleteAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken ct = default);
    Task<TrimResult?> TrimAsync(ProcessInfo proc, CancellationToken ct = default);
    Task<TerminateResult?> TerminateAsync(ProcessInfo proc, bool force, CancellationToken ct = default);
    Task<RamSnapshot?> GetRamSnapshotAsync(CancellationToken ct = default);
    Task<string?> RequestExecutionAsync(CancellationToken ct = default);
    Task<bool> ConfirmExecutionAsync(string token, CancellationToken ct = default);
    Task<bool> ResetExecutionAsync(CancellationToken ct = default);

    Task<IReadOnlyList<WinGetPackage>> GetOutdatedPackagesAsync(CancellationToken ct = default);
    IAsyncEnumerable<string> UpgradePackageAsync(string packageId, bool upgradeAll, CancellationToken ct = default);

    Task<IReadOnlyList<ContextMenuEntry>> GetContextMenuEntriesAsync(CancellationToken ct = default);
    Task<bool> SetContextMenuEnabledAsync(ContextMenuEntry entry, bool enable, CancellationToken ct = default);
    Task<bool> DeleteContextMenuEntryAsync(ContextMenuEntry entry, CancellationToken ct = default);

    Task<IReadOnlyList<AppxPackageEntry>> GetAppxPackagesAsync(CancellationToken ct = default);
    Task<bool> RemoveAppxPackageAsync(string packageFullName, CancellationToken ct = default);
    Task<IReadOnlyList<TelemetryTweak>> GetTelemetryTweaksAsync(CancellationToken ct = default);
    Task<bool> ApplyTweakAsync(TelemetryTweak tweak, bool enable, CancellationToken ct = default);

    IAsyncEnumerable<object> ScanLargeFilesAsync(MftScanRequest req, CancellationToken ct = default);
    Task<long> DeleteMftFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default);

    Task<PerformanceProfile?> GetPerformanceProfileAsync(CancellationToken ct = default);
    Task<bool> ApplyPerformanceTuneAsync(PerformanceTuneRequest req, CancellationToken ct = default);

    Task<IReadOnlyList<StartupItem>> GetStartupItemsAsync(CancellationToken ct = default);
    Task<bool> SetStartupStatusAsync(StartupItem item, StartupItemStatus newStatus, int delayMinutes = 0, CancellationToken ct = default);

    Task<IReadOnlyList<InstalledApp>> GetInstalledAppsAsync(CancellationToken ct = default);
    IAsyncEnumerable<UninstallProgress> UninstallAsync(InstalledApp app, CancellationToken ct = default);
    Task<LeftoverScanResult?> ScanLeftoversAsync(InstalledApp app, CancellationToken ct = default);
    IAsyncEnumerable<UninstallProgress> DeleteLeftoversAsync(IReadOnlyList<LeftoverCandidate> candidates, CancellationToken ct = default);

    Task<IReadOnlyList<DriverInfo>> GetDriversAsync(CancellationToken ct = default);
    Task<DriverExportResult?> ExportDriversAsync(CancellationToken ct = default);

    Task<DawScanResult?> ScanDawAsync(CancellationToken ct = default);
    Task<long> DeleteDawCachesAsync(IReadOnlyList<string> cachePaths, CancellationToken ct = default);

    Task<IReadOnlyList<AudioEndpointInfo>> GetAudioEndpointsAsync(CancellationToken ct = default);
    Task<bool> RestartAudioServiceAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PrivacyAppEntry>> GetPrivacyEntriesAsync(string device, CancellationToken ct = default);
    Task<bool> SetPrivacyAccessAsync(string device, string appName, bool allow, CancellationToken ct = default);

    IAsyncEnumerable<NetworkFlushStep> FlushNetworkAsync(NetworkFlushRequest req, CancellationToken ct = default);

    Task<ComponentStoreAnalysis?> AnalyzeComponentStoreAsync(CancellationToken ct = default);
    IAsyncEnumerable<string> RunDismCleanupAsync(bool resetBase, CancellationToken ct = default);
    IAsyncEnumerable<string> RunSfcAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DriveHealth>> GetDriveHealthAsync(CancellationToken ct = default);
    Task<bool> EnableTrimAsync(CancellationToken ct = default);
}