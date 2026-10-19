using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DClean.Contracts;
using DClean.UI.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace DClean.UI.ViewModels;

public sealed partial class UninstallerViewModel : ObservableObject
{
    private readonly IPipeClient _client;

    [ObservableProperty] private bool   _isLoading        = false;
    [ObservableProperty] private bool   _isUninstalling   = false;
    [ObservableProperty] private bool   _isScanning       = false;
    [ObservableProperty] private string _statusText       = "בחר יישום להסרת התקנה";
    [ObservableProperty] private int    _progress         = 0;
    [ObservableProperty] private string _filterText       = string.Empty;
    [ObservableProperty] private InstalledAppViewModel? _selectedApp;
    [ObservableProperty] private LeftoverItemViewModel? _selectedLeftover;

    public ObservableCollection<InstalledAppViewModel>  Apps      { get; } = [];
    public ObservableCollection<InstalledAppViewModel>  Filtered  { get; } = [];
    public ObservableCollection<LeftoverItemViewModel>  Leftovers { get; } = [];
    public ObservableCollection<string>                 Log       { get; } = [];

    public UninstallerViewModel(IPipeClient client)
    {
        _client = client;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FilterText)) ApplyFilter();
        };
    }

    // ── Load app list ─────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadAppsAsync()
    {
        if (!await _client.EnsureConnectedAsync())
        {
            StatusText = "לא ניתן להתחבר לשירות";
            return;
        }
        IsLoading  = true;
        StatusText = "טוען רשימת יישומים...";
        Apps.Clear();
        Filtered.Clear();

        try
        {
            var apps = await _client.GetInstalledAppsAsync();
            foreach (var app in apps)
                Apps.Add(new InstalledAppViewModel(app));
            ApplyFilter();
            StatusText = $"נמצאו {apps.Count} יישומים מותקנים";
        }
        catch (Exception ex)
        {
            StatusText = $"שגיאה בטעינה: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    private void ApplyFilter()
    {
        Filtered.Clear();
        string q = FilterText.Trim().ToLowerInvariant();
        foreach (var vm in Apps)
        {
            if (string.IsNullOrEmpty(q)
             || vm.DisplayName.ToLowerInvariant().Contains(q)
             || vm.Publisher.ToLowerInvariant().Contains(q))
                Filtered.Add(vm);
        }
    }

    // ── Uninstall ─────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync()
    {
        if (SelectedApp is null) return;

        var confirm = MessageBox.Show(
            $"האם להסיר את \"{SelectedApp.DisplayName}\"?\n\nה-uninstaller הרשמי יופעל תחילה.",
            "אישור הסרת התקנה",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        IsUninstalling = true;
        Progress       = 0;
        Log.Clear();
        Leftovers.Clear();
        StatusText = "מסיר התקנה...";
        var app = SelectedApp.App;

        try
        {
            bool completedWithError = false;
            await foreach (var p in _client.UninstallAsync(app))
            {
                AddLog(p.Message);
                Progress = p.PercentComplete;
                completedWithError = p.IsCompleted && p.IsError;
            }

            if (completedWithError)
            {
                AddLog("⚠️ ההסרה נעצרה מטעמי בטיחות");
                StatusText = $"ההסרה של {app.DisplayName} נעצרה";
                return;
            }

            AddLog("✅ הסרת ההתקנה הושלמה");
            StatusText = $"הוסרה: {app.DisplayName}";

            // Remove from list
            var vm = Apps.FirstOrDefault(a => a.App == app);
            if (vm != null) { Apps.Remove(vm); ApplyFilter(); }

            // Auto-scan for leftovers
            await ScanLeftoversInternalAsync(app);
        }
        catch (Exception ex)
        {
            AddLog($"❌ שגיאה: {ex.Message}");
            StatusText = $"שגיאה: {ex.Message}";
        }
        finally { IsUninstalling = false; }
    }

    private bool CanUninstall() => SelectedApp != null && !IsUninstalling && !IsScanning;

    // ── Leftover scan ─────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanScanLeftovers))]
    private async Task ScanLeftoversAsync()
    {
        if (SelectedApp is null) return;
        await ScanLeftoversInternalAsync(SelectedApp.App);
    }

    private bool CanScanLeftovers() => SelectedApp != null && !IsUninstalling && !IsScanning;

    private async Task ScanLeftoversInternalAsync(InstalledApp app)
    {
        IsScanning = true;
        Leftovers.Clear();
        SelectedLeftover = null;
        AddLog("🔍 מחפש שאריות...");

        try
        {
            var result = await _client.ScanLeftoversAsync(app);
            if (result == null) { AddLog("שאריות לא נמצאו"); return; }

            foreach (var c in result.Candidates)
                Leftovers.Add(new LeftoverItemViewModel(c));

            SelectedLeftover = Leftovers.FirstOrDefault();

            string sizeStr = FormatHelper.FormatBytes(result.TotalBytes);
            AddLog($"נמצאו {result.Candidates.Count} שאריות ({sizeStr})");
            StatusText = result.Candidates.Count > 0
                ? $"נמצאו {result.Candidates.Count} שאריות — {sizeStr}"
                : "לא נמצאו שאריות";
        }
        catch (Exception ex)
        {
            AddLog($"❌ שגיאת סריקה: {ex.Message}");
        }
        finally { IsScanning = false; }
    }

    // ── Delete leftovers ──────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanDeleteLeftovers))]
    private async Task DeleteLeftoversAsync()
    {
        var selected = Leftovers.Where(l => l.IsSelected).Select(l => l.Candidate).ToList();
        if (selected.Count == 0) return;

        var confirm = MessageBox.Show(
            $"למחוק {selected.Count} שאריות?\n\nגיבוי ריג'יסטרי יישמר לפני המחיקה.",
            "אישור מחיקת שאריות",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        IsUninstalling = true;
        try
        {
            await foreach (var p in _client.DeleteLeftoversAsync(selected))
            {
                AddLog(p.Message);
                Progress = p.PercentComplete;
            }

            foreach (var item in Leftovers.Where(l => l.IsSelected).ToList())
                Leftovers.Remove(item);

            StatusText = "מחיקת שאריות הושלמה";
        }
        catch (Exception ex) { AddLog($"❌ שגיאה: {ex.Message}"); }
        finally { IsUninstalling = false; }
    }

    private bool CanDeleteLeftovers() =>
        Leftovers.Any(l => l.IsSelected) && !IsUninstalling && !IsScanning;

    [RelayCommand]
    private void SelectAllLeftovers()
    {
        foreach (var l in Leftovers) l.IsSelected = true;
        DeleteLeftoversCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void DeselectAllLeftovers()
    {
        foreach (var l in Leftovers) l.IsSelected = false;
        DeleteLeftoversCommand.NotifyCanExecuteChanged();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void AddLog(string msg) =>
        Application.Current.Dispatcher.BeginInvoke(() => Log.Insert(0, msg));
}

// ── Sub-ViewModels ─────────────────────────────────────────────────────────────

public sealed partial class InstalledAppViewModel(InstalledApp app) : ObservableObject
{
    public InstalledApp App          => app;
    public string DisplayName        => app.DisplayName;
    public string Publisher          => app.Publisher;
    public string Version            => app.Version;
    public string InstallLocation    => string.IsNullOrWhiteSpace(app.InstallLocation) ? "לא זוהה" : app.InstallLocation;
    public string UninstallCommand   => string.IsNullOrWhiteSpace(app.QuietUninstallString) ? app.UninstallString : app.QuietUninstallString!;
    public string DiscoverySource    => string.IsNullOrWhiteSpace(app.DiscoverySource) ? "לא זוהה" : app.DiscoverySource;
    public string RegistryKeyPath    => string.IsNullOrWhiteSpace(app.RegistryKeyPath) ? "לא זוהה" : app.RegistryKeyPath;
    public string SizeText  => app.EstimatedSizeKb > 0
        ? $"{app.EstimatedSizeKb / 1024.0:F1} MB" : "—";
    public string TypeBadge  => app.Type.ToString();
    public Brush  TypeBrush  => MakeBrush(app.Type switch
    {
        InstallerType.MSI       => "#00D4A0",
        InstallerType.Squirrel  => "#F0A500",
        InstallerType.InnoSetup => "#7B8FD4",
        InstallerType.NSIS      => "#7B8FD4",
        _                       => "#5A6478",
    });

    private static SolidColorBrush MakeBrush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));
}

public sealed partial class LeftoverItemViewModel(LeftoverCandidate candidate) : ObservableObject
{
    public LeftoverCandidate Candidate => candidate;
    public string Path                 => candidate.Path;
    public string Reason               => candidate.Reason;
    public string Source               => string.IsNullOrWhiteSpace(candidate.Source) ? "לא זוהה" : candidate.Source;
    public string RelatedApplication   => string.IsNullOrWhiteSpace(candidate.RelatedApplication) ? "לא זוהה" : candidate.RelatedApplication;
    public string Location             => string.IsNullOrWhiteSpace(candidate.Location) ? candidate.Path : candidate.Location;
    public string ProblematicItem      => string.IsNullOrWhiteSpace(candidate.ProblematicItem) ? candidate.Path : candidate.ProblematicItem;
    public string TypeIcon             => candidate.Type switch
    {
        LeftoverType.Directory     => "📁",
        LeftoverType.File          => "📄",
        LeftoverType.RegistryKey   => "🔑",
        LeftoverType.Service       => "⚙️",
        LeftoverType.ScheduledTask => "📋",
        _                          => "•",
    };
    public string ScoreText  => $"{candidate.Score * 100:F0}%";
    public Brush  ScoreBrush => MakeBrush(
        candidate.Score >= 0.85f ? "#FF4D5B" :
        candidate.Score >= 0.65f ? "#F0A500" : "#5A6478");

    [ObservableProperty] private bool _isSelected = true;

    private static SolidColorBrush MakeBrush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));
}
