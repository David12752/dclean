namespace DClean.Contracts;

// ── Operation mode ───────────────────────────────────────────────────────────

public enum OperationMode { DryRun = 0, Execute = 1 }

// ── Scan / Delete ────────────────────────────────────────────────────────────

public record ScanRequest(OperationMode Mode, IReadOnlyList<string> Targets);

public record ScanResult(
    string Path,
    long SizeBytes,
    bool Blocked,
    string BlockReason);

public enum DeleteStatus { DryRun, Deleted, Blocked, AccessDenied, ScheduledReboot, Error }

public record DeleteOutcome(
    string Path,
    DeleteStatus Status,
    string Detail,
    long BytesFreed);

// ── Process management ───────────────────────────────────────────────────────

public record ProcessInfo(
    int Pid,
    string Name,
    long WorkingSetBytes,
    long PrivateBytesKb,
    bool IsCritical,
    bool IsProtected);

public record TrimResult(int Pid, string ProcessName, bool Success, string Detail);

public record TerminateResult(int Pid, string ProcessName, bool Success, string Detail);

// ── Memory snapshot ──────────────────────────────────────────────────────────

public record RamSnapshot(
    long TotalBytes,
    long AvailableBytes,
    uint UsedPercent,
    float CommitChargePercent);

// ── Cache discovery ──────────────────────────────────────────────────────────

public record CacheTarget(string BrowserName, string FullPath, long EstimatedBytes);

// ── Uninstaller ──────────────────────────────────────────────────────────────

public enum InstallerType { MSI, Squirrel, InnoSetup, NSIS, ExeGeneric }

public enum LeftoverType { File, Directory, RegistryKey, Service, ScheduledTask, ShellExtension }

public record InstalledApp(
    string?       Guid,
    string        DisplayName,
    string        Publisher,
    string        Version,
    string?       InstallLocation,
    string        UninstallString,
    string?       QuietUninstallString,
    long          EstimatedSizeKb,
    InstallerType Type,
    string?       DisplayIconPath,
    string        DiscoverySource = "",
    string        RegistryKeyPath = "");

public record LeftoverCandidate(
    string      Path,
    LeftoverType Type,
    float        Score,
    string       Reason,
    string       Source = "",
    string       RelatedApplication = "",
    string       Location = "",
    string       ProblematicItem = "");

public record UninstallRequest(InstalledApp App, bool ScanLeftovers);

public record UninstallProgress(
    string Message,
    int PercentComplete,
    bool IsError = false,
    bool IsCompleted = false);

public record LeftoverScanResult(
    IReadOnlyList<LeftoverCandidate> Candidates,
    long TotalBytes);

public record LeftoverDeleteRequest(IReadOnlyList<LeftoverCandidate> ToDelete);

// ── Startup & Service Optimizer ──────────────────────────────────────────────

public enum StartupItemSource
{
    RegistryRunHklm, RegistryRunHkcu,
    RegistryRunOncHklm, RegistryRunOnceHkcu,
    StartupFolderUser, StartupFolderCommon,
    ScheduledTask, WindowsService
}

public enum StartupItemStatus { Enabled, Disabled, DelayedAutoStart }

public record StartupItem(
    string            Name,
    string            Command,
    StartupItemSource Source,
    StartupItemStatus Status,
    string            Publisher,
    string?           RegistryPath,
    int               DelayMinutes);

public record SetStartupStatusRequest(StartupItem Item, StartupItemStatus NewStatus, int DelayMinutes);

// ── WinGet Updater ────────────────────────────────────────────────────────────

public record WinGetPackage(
    string Name,
    string Id,
    string InstalledVersion,
    string AvailableVersion,
    string Source,
    bool   HasUpdate);

public record WinGetUpgradeRequest(string PackageId, bool UpgradeAll);

// ── Context Menu Manager ──────────────────────────────────────────────────────

public enum ContextMenuEntryType
{
    FileShell, FileShellEx,
    FolderShell, FolderShellEx,
    BackgroundShell, BackgroundShellEx,
    DriveShell, AllFilesShellEx,
    SendTo
}

public record ContextMenuEntry(
    string               Id,
    string               DisplayName,
    string               SubKeyName,
    string               RegistryPath,
    string?              Command,
    ContextMenuEntryType Type,
    bool                 IsEnabled);

public record SetContextMenuEnabledRequest(ContextMenuEntry Entry, bool Enable);

// ── Debloater ─────────────────────────────────────────────────────────────────

public record AppxPackageEntry(
    string Name,
    string Version,
    string PackageFullName,
    bool   IsSafeToRemove);

public sealed class TelemetryTweak(
    string  Name,
    string  Description,
    string  Category,
    string  RegistryPath,
    string  ValueName,
    object  DisableValue,
    object? EnableValue,
    bool    DisablesFeature,
    bool    IsCurrentlyEnabled)
{
    public string  Name              { get; } = Name;
    public string  Description       { get; } = Description;
    public string  Category          { get; } = Category;
    public string  RegistryPath      { get; } = RegistryPath;
    public string  ValueName         { get; } = ValueName;
    public object  DisableValue      { get; } = DisableValue;
    public object? EnableValue       { get; } = EnableValue;
    public bool    DisablesFeature   { get; } = DisablesFeature;
    public bool    IsCurrentlyEnabled { get; set; } = IsCurrentlyEnabled;
}

public record RemoveAppxRequest(string PackageFullName);
public record ApplyTweakRequest(TelemetryTweak Tweak, bool Enable);

// ── Large File & Duplicate Scanner ───────────────────────────────────────────

public record LargeFileEntry(
    string   Path,
    long     SizeBytes,
    DateTime LastModifiedUtc,
    string   Extension);

public record DuplicateGroup(
    string                   Hash,
    IReadOnlyList<LargeFileEntry> Files,
    long                     WastedBytes);

public record ScanProgressInfo(string CurrentPath, int FilesFound, long LastSize);

public record MftScanRequest(
    IReadOnlyList<string> Roots,
    long   MinSizeBytes,
    bool   FindDuplicates);

public record MftScanResult(
    IReadOnlyList<LargeFileEntry> LargeFiles,
    IReadOnlyList<DuplicateGroup> DuplicateGroups,
    long                          TotalBytes);

public record MftDeleteRequest(IReadOnlyList<string> Paths);

// ── Performance Tuner ────────────────────────────────────────────────────────

public record PerformanceProfile(
    string ActivePlanName,
    bool   UsbSelectiveSuspendEnabled,
    bool   CoreParkingEnabled);

public record PerformanceTuneRequest(
    bool ApplyUltimatePlan,
    bool DisableUsbSuspend,
    bool DisableCoreParking);

// ── Phase 7A: Driver Auditor ──────────────────────────────────────────────────

public enum DriverStatus { Unknown, UpToDate, UpdateAvailable, Unsigned, Problem }

public record DriverInfo(
    string    DeviceName,
    string    Version,
    DateTime? DriverDate,
    string    Provider,
    bool      IsSigned,
    string    DeviceClass,
    string    InfName,
    DriverStatus Status);

public record DriverExportResult(bool Success, string ExportPath);

// ── Phase 9H: DAW Cleaner ─────────────────────────────────────────────────────

public enum PluginStatus { Valid, Suspicious, Orphan, Invalid }

public record PluginEntry(
    string Path,
    string Format,
    string Name,
    long   SizeBytes,
    PluginStatus Status);

public sealed class DawCacheEntry(
    string Daw, string CachePath, string Description, long SizeBytes)
{
    public string Daw         { get; } = Daw;
    public string CachePath   { get; } = CachePath;
    public string Description { get; } = Description;
    public long   SizeBytes   { get; } = SizeBytes;
    public List<string>? FileList { get; set; }
}

public record BrokenSampleLibrary(string Publisher, string LibraryName, string BrokenPath);

public record DawScanResult(
    IReadOnlyList<PluginEntry>         Plugins,
    IReadOnlyList<DawCacheEntry>       Caches,
    IReadOnlyList<BrokenSampleLibrary> BrokenLibraries);

public record DawDeleteRequest(IReadOnlyList<string> CachePaths);

// ── Phase 11I: Audio Endpoint Reset ───────────────────────────────────────────

public record AudioEndpointInfo(
    string Id,
    string FriendlyName,
    string Description,
    bool   IsExclusiveLocked);

public record PrivacyAppEntry(
    string AppName,
    bool   IsAllowed,
    string Device);

public record SetPrivacyRequest(string Device, string AppName, bool Allow);

// ── Phase 11J: Network Flush ──────────────────────────────────────────────────

public record NetworkFlushStep(
    string StepName,
    string Detail,
    bool   Success,
    bool   IsRunning,
    bool   RequiresReboot);

public record NetworkFlushRequest(
    bool FlushDns,
    bool ReleaseRenewDhcp,
    bool FlushArp,
    bool ResetWinsock,
    bool ResetTcpIp);

// ── Phase 12K: WinSxS + SFC ──────────────────────────────────────────────────

public record ComponentStoreAnalysis(
    long   StoreSizeBytes,
    long   ReclaimableBytes,
    string Recommendation);

public record DismCleanupRequest(bool ResetBase);

// ── Phase 12L: SSD S.M.A.R.T. Analyzer ──────────────────────────────────────

public record SmartAttributeEntry(
    byte   Id,
    string Name,
    byte   Value,
    byte   Worst,
    byte   Threshold,
    long   RawValue,
    bool   IsCritical,
    bool   IsHealthy);

public record DriveHealth(
    string   Model,
    string   SerialNumber,
    long     CapacityBytes,
    int      HealthScore,
    int      TemperatureCelsius,
    long     PowerOnHours,
    long     TotalBytesWritten,
    bool     TrimEnabled,
    IReadOnlyList<SmartAttributeEntry> Attributes);

// ── IPC pipe messages ────────────────────────────────────────────────────────

public enum PipeMessageType
{
    ScanRequest,
    ScanProgress,
    ScanComplete,
    DeleteRequest,
    DeleteResult,
    ProcessListRequest,
    ProcessListResult,
    TrimRequest,
    TrimResult,
    TerminateRequest,
    TerminateResult,
    RamSnapshotRequest,
    RamSnapshotResult,
    RequestExecutionRequest,
    RequestExecutionResult,
    ConfirmExecutionRequest,
    ConfirmExecutionResult,
    ResetExecutionRequest,
    ResetExecutionResult,
    // Phase 9G — WinGet Updater
    WinGetOutdatedRequest,
    WinGetOutdatedResult,
    WinGetUpgradeRequest,
    WinGetUpgradeProgress,
    WinGetUpgradeResult,
    // Phase 9F — Context Menu Manager
    ContextMenuEntriesRequest,
    ContextMenuEntriesResult,
    SetContextMenuEnabledRequest,
    SetContextMenuEnabledResult,
    DeleteContextMenuEntryRequest,
    DeleteContextMenuEntryResult,
    // Phase 8E — Debloater
    AppxPackagesRequest,
    AppxPackagesResult,
    RemoveAppxRequest,
    RemoveAppxResult,
    TelemetryTweaksRequest,
    TelemetryTweaksResult,
    ApplyTweakRequest,
    ApplyTweakResult,
    // Phase 8D — Large File & Duplicate Scanner
    MftScanRequest,
    MftScanProgress,
    MftScanResult,
    MftDeleteRequest,
    MftDeleteResult,
    // Phase 8C — Performance Tuner
    PerformanceProfileRequest,
    PerformanceProfileResult,
    PerformanceTuneRequest,
    PerformanceTuneResult,
    // Phase 7 — Startup & Service Optimizer
    StartupItemsRequest,
    StartupItemsResult,
    SetStartupStatusRequest,
    SetStartupStatusResult,
    // Phase 6 — Uninstaller
    InstalledAppsRequest,
    InstalledAppsResult,
    UninstallRequest,
    UninstallProgress,
    UninstallComplete,
    LeftoverScanRequest,
    LeftoverScanResult,
    LeftoverDeleteRequest,
    LeftoverDeleteResult,
    Error,
    // Phase 7A — Driver Auditor
    DriverAuditRequest,
    DriverAuditResult,
    DriverExportRequest,
    DriverExportResult,
    // Phase 9H — DAW Cleaner
    DawScanRequest,
    DawScanResult,
    DawDeleteRequest,
    DawDeleteResult,
    // Phase 11I — Audio Endpoint Reset
    AudioEndpointsRequest,
    AudioEndpointsResult,
    RestartAudioServiceRequest,
    RestartAudioServiceResult,
    PrivacyEntriesRequest,
    PrivacyEntriesResult,
    SetPrivacyRequest,
    SetPrivacyResult,
    // Phase 11J — Network Flush
    NetworkFlushRequest,
    NetworkFlushProgress,
    NetworkFlushComplete,
    // Phase 12K — WinSxS + SFC
    DismAnalyzeRequest,
    DismAnalyzeResult,
    DismCleanupRequest,
    DismCleanupProgress,
    DismCleanupComplete,
    SfcScanRequest,
    SfcScanProgress,
    SfcScanComplete,
    // Phase 12L — SSD S.M.A.R.T.
    SmartAnalyzeRequest,
    SmartAnalyzeResult,
    EnableTrimRequest,
    EnableTrimResult
}

public record PipeMessage(PipeMessageType Type, string JsonPayload);
