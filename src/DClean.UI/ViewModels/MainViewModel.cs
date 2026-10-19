using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DClean.UI.Services;

namespace DClean.UI.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private ObservableObject _currentView;
    [ObservableProperty] private string _activeNav = "dashboard";

    public DashboardViewModel    Dashboard   { get; }
    public ProcessViewModel      Processes   { get; }
    public SettingsViewModel     Settings    { get; }
    public StartupViewModel      Startup     { get; }
    public LogViewModel          Log         { get; }
    public UninstallerViewModel      Uninstaller  { get; }
    public PerformanceTunerViewModel  PerfTuner    { get; }
    public MftScannerViewModel         MftScanner   { get; }
    public DebloaterViewModel            Debloater    { get; }
    public ContextMenuViewModel           ContextMenu  { get; }
    public WinGetUpdaterViewModel          WinGet       { get; }
    public DriverAuditorViewModel         DriverAuditor { get; }
    public DawCleanerViewModel            DawCleaner    { get; }
    public AudioEndpointViewModel         AudioEndpoint { get; }
    public NetworkFlushViewModel          NetworkFlush  { get; }
    public ComponentStoreViewModel        CompStore     { get; }
    public SmartAnalyzerViewModel         SmartAnalyzer { get; }
    public FullScanViewModel              FullScan      { get; }

    public MainViewModel(
        DashboardViewModel dashboard,
        ProcessViewModel processes,
        SettingsViewModel settings,
        StartupViewModel startup,
        LogViewModel log,
        UninstallerViewModel uninstaller,
        PerformanceTunerViewModel perfTuner,
        MftScannerViewModel mftScanner,
        DebloaterViewModel debloater,
        ContextMenuViewModel contextMenu,
        WinGetUpdaterViewModel winGet,
        DriverAuditorViewModel driverAuditor,
        DawCleanerViewModel dawCleaner,
        AudioEndpointViewModel audioEndpoint,
        NetworkFlushViewModel networkFlush,
        ComponentStoreViewModel compStore,
        SmartAnalyzerViewModel smartAnalyzer,
        FullScanViewModel fullScan)
    {
        Dashboard     = dashboard;
        Processes     = processes;
        Settings      = settings;
        Startup       = startup;
        Log           = log;
        Uninstaller   = uninstaller;
        PerfTuner     = perfTuner;
        MftScanner    = mftScanner;
        Debloater     = debloater;
        ContextMenu   = contextMenu;
        WinGet        = winGet;
        DriverAuditor = driverAuditor;
        DawCleaner    = dawCleaner;
        AudioEndpoint = audioEndpoint;
        NetworkFlush  = networkFlush;
        CompStore     = compStore;
        SmartAnalyzer = smartAnalyzer;
        FullScan      = fullScan;
        FullScan.NavigateToTarget = NavigateTo;
        _currentView = FullScan;
    }

    private void NavigateTo(string navTarget)
    {
        switch (navTarget)
        {
            case "dashboard":
                NavigateDashboard();
                break;
            case "startup":
                NavigateStartup();
                break;
            case "winget":
                NavigateWinGetUpdater();
                break;
            case "drivers":
                NavigateDriverAuditor();
                break;
            case "smart":
                NavigateSmartAnalyzer();
                break;
            case "compstore":
                NavigateComponentStore();
                break;
            case "daw":
                NavigateDawCleaner();
                break;
            case "audio":
                NavigateAudioEndpoint();
                break;
            case "processes":
                NavigateProcesses();
                break;
            case "performance":
                NavigatePerformanceTuner();
                break;
        }
    }

    [RelayCommand]
    private void NavigateDashboard()
    {
        ActiveNav   = "dashboard";
        CurrentView = Dashboard;
    }

    [RelayCommand]
    private void NavigateProcesses()
    {
        ActiveNav   = "processes";
        CurrentView = Processes;
        _ = Processes.RefreshCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void NavigateSettings()
    {
        ActiveNav   = "settings";
        CurrentView = Settings;
    }

    [RelayCommand]
    private void NavigateStartup()
    {
        ActiveNav   = "startup";
        CurrentView = Startup;
        if (!Startup.AllItems.Any())
            _ = Startup.LoadCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void NavigateLog()
    {
        ActiveNav   = "log";
        CurrentView = Log;
    }

    [RelayCommand]
    private void NavigateUninstaller()
    {
        ActiveNav   = "uninstaller";
        CurrentView = Uninstaller;
        if (!Uninstaller.Apps.Any())
            _ = Uninstaller.LoadAppsCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void NavigatePerformanceTuner()
    {
        ActiveNav   = "performance";
        CurrentView = PerfTuner;
        _ = PerfTuner.LoadProfileCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void NavigateMftScanner()
    {
        ActiveNav   = "mftscanner";
        CurrentView = MftScanner;
        _ = Task.CompletedTask; // user presses Scan manually
    }

    [RelayCommand]
    private void NavigateDebloater()
    {
        ActiveNav   = "debloater";
        CurrentView = Debloater;
        if (!Debloater.Packages.Any() && !Debloater.Tweaks.Any())
            _ = Debloater.LoadCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void NavigateContextMenu()
    {
        ActiveNav   = "contextmenu";
        CurrentView = ContextMenu;
        if (!ContextMenu.Entries.Any())
            _ = ContextMenu.LoadCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void NavigateWinGetUpdater()
    {
        ActiveNav   = "winget";
        CurrentView = WinGet;
    }

    [RelayCommand]
    private void NavigateFullScan()
    {
        ActiveNav   = "fullscan";
        CurrentView = FullScan;
    }

    [RelayCommand]
    private void NavigateDriverAuditor()
    {
        ActiveNav   = "drivers";
        CurrentView = DriverAuditor;
    }

    [RelayCommand]
    private void NavigateDawCleaner()
    {
        ActiveNav   = "daw";
        CurrentView = DawCleaner;
    }

    [RelayCommand]
    private void NavigateAudioEndpoint()
    {
        ActiveNav   = "audio";
        CurrentView = AudioEndpoint;
        if (!AudioEndpoint.Endpoints.Any())
            _ = AudioEndpoint.LoadCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void NavigateNetworkFlush()
    {
        ActiveNav   = "network";
        CurrentView = NetworkFlush;
    }

    [RelayCommand]
    private void NavigateComponentStore()
    {
        ActiveNav   = "compstore";
        CurrentView = CompStore;
    }

    [RelayCommand]
    private void NavigateSmartAnalyzer()
    {
        ActiveNav   = "smart";
        CurrentView = SmartAnalyzer;
    }
}
