using DClean.Engine.Audio;
using DClean.Engine.Cleaning;
using DClean.Engine.DAW;
using DClean.Engine.Drivers;
using DClean.Engine.Network;
using DClean.Engine.OS;
using DClean.Engine.Memory;
using DClean.Engine.Safety;
using DClean.Engine.Debloat;
using DClean.Engine.Shell;
using DClean.Engine.Performance;
using DClean.Engine.Storage;
using DClean.Engine.Startup;
using DClean.Engine.Uninstaller;
using DClean.Service;

var builder = Host.CreateApplicationBuilder(args);

// Register as Windows Service (no-op when run interactively)
builder.Services.AddWindowsService(o => o.ServiceName = "DCleanService");

// Safety layer
builder.Services.AddSingleton<DryRunGuard>();
builder.Services.AddSingleton<PathValidator>();

// Cleaning engine
builder.Services.AddSingleton<BrowserCacheLocator>();
builder.Services.AddSingleton<FileScanner>();
builder.Services.AddSingleton<SafeDeleter>();

// Memory & process manager
builder.Services.AddSingleton<ProcessEnumerator>();
builder.Services.AddSingleton<WorkingSetTrimmer>();
builder.Services.AddSingleton<SafeTerminator>();
builder.Services.AddSingleton<RamMonitor>();

// Phase 9G — WinGet updater
builder.Services.AddSingleton<WinGetUpdater>();

// Phase 9F — Context menu manager
builder.Services.AddSingleton<ContextMenuEngine>();

// Phase 8E — Debloater
builder.Services.AddSingleton<Debloater>();

// Phase 8D — MFT / large-file scanner
builder.Services.AddSingleton<MftScanner>();

// Phase 8C — Performance tuner
builder.Services.AddSingleton<PerformanceTuner>();

// Phase 7 — Startup engine
builder.Services.AddSingleton<StartupEngine>();

// Phase 7A — Driver auditor
builder.Services.AddSingleton<DriverAuditor>();

// Phase 9H — DAW cleaner
builder.Services.AddSingleton<DawCleaner>();

// Phase 11I — Audio endpoint reset
builder.Services.AddSingleton<AudioEndpointEngine>();

// Phase 11J — Network flush
builder.Services.AddSingleton<NetworkFlusher>();

// Phase 12K — Component store
builder.Services.AddSingleton<ComponentStoreEngine>();

// Phase 12L — SSD S.M.A.R.T.
builder.Services.AddSingleton<SmartAnalyzer>();

// Phase 6 — Uninstaller engine
builder.Services.AddSingleton<InstalledAppDiscovery>();
builder.Services.AddSingleton<LeftoverScanner>();
builder.Services.AddSingleton<SafeUninstaller>();

// IPC pipe server
builder.Services.AddSingleton<PipeServer>();

// Hosted worker
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
