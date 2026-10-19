using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DClean.Contracts;
using DClean.UI.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace DClean.UI.ViewModels;

public sealed partial class DebloaterViewModel : ObservableObject
{
    private readonly IPipeClient _client;

    [ObservableProperty] private bool   _isLoading   = false;
    [ObservableProperty] private bool   _isBusy      = false;
    [ObservableProperty] private string _statusText  = "לחץ 'טען' לטעינת נתונים";
    [ObservableProperty] private int    _activeTab   = 0; // 0=Appx, 1=Tweaks

    public ObservableCollection<AppxPackageViewModel>  Packages { get; } = [];
    public ObservableCollection<TelemetryTweakViewModel> Tweaks { get; } = [];

    public DebloaterViewModel(IPipeClient client)
    {
        _client = client;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (!await _client.EnsureConnectedAsync()) { StatusText = "לא ניתן להתחבר לשירות"; return; }
        IsLoading = true;
        Packages.Clear();
        Tweaks.Clear();

        try
        {
            var packages = await _client.GetAppxPackagesAsync();
            foreach (var p in packages) Packages.Add(new AppxPackageViewModel(p));

            var tweaks = await _client.GetTelemetryTweaksAsync();
            foreach (var t in tweaks) Tweaks.Add(new TelemetryTweakViewModel(t, this));

            StatusText = $"{Packages.Count} חבילות Appx · {Tweaks.Count} שיפורי פרטיות";
        }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private void SetActiveTab(object? param)
    {
        if (param is int t) ActiveTab = t;
        else if (param is string s && int.TryParse(s, out int n)) ActiveTab = n;
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task RemoveSelectedPackagesAsync()
    {
        var toRemove = Packages.Where(p => p.IsSelected).ToList();
        if (toRemove.Count == 0) return;

        var confirm = MessageBox.Show(
            $"להסיר {toRemove.Count} חבילות Appx?\n" +
            "ניתן לשחזר מ-Microsoft Store אם תצטרך.",
            "אישור הסרה", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        IsBusy     = true;
        int ok = 0, fail = 0;

        foreach (var pkg in toRemove)
        {
            StatusText = $"מסיר: {pkg.Name}...";
            bool success = await _client.RemoveAppxPackageAsync(pkg.PackageFullName);
            if (success) { Packages.Remove(pkg); ok++; }
            else fail++;
        }

        StatusText = $"הוסרו {ok} חבילות{(fail > 0 ? $" · נכשלו {fail}" : string.Empty)}";
        IsBusy = false;
    }

    private bool CanAct() => !IsBusy;

    internal async Task ApplyTweakAsync(TelemetryTweakViewModel vm, bool enable)
    {
        IsBusy     = true;
        StatusText = $"מחיל: {vm.Name}...";
        bool ok = await _client.ApplyTweakAsync(vm.Tweak, enable);
        if (ok) vm.IsEnabled = enable;
        StatusText = ok ? $"הוחל: {vm.Name}" : $"כשלון: {vm.Name}";
        IsBusy = false;
    }
}

// ── Sub-ViewModels ────────────────────────────────────────────────────────────

public sealed partial class AppxPackageViewModel(AppxPackageEntry entry) : ObservableObject
{
    public AppxPackageEntry Entry          => entry;
    public string  Name                    => entry.Name;
    public string  Version                 => entry.Version;
    public string  PackageFullName         => entry.PackageFullName;
    public bool    IsSafeToRemove          => entry.IsSafeToRemove;
    public string  SafetyLabel  => entry.IsSafeToRemove ? "בטוח להסרה" : "מערכת";
    public Brush   SafetyBrush  => new SolidColorBrush((Color)ColorConverter.ConvertFromString(
        entry.IsSafeToRemove ? "#00D4A0" : "#5A6478"));

    [ObservableProperty] private bool _isSelected = entry.IsSafeToRemove;
}

public sealed partial class TelemetryTweakViewModel : ObservableObject
{
    private readonly DebloaterViewModel _parent;

    public TelemetryTweak Tweak    { get; }
    public string  Name            => Tweak.Name;
    public string  Description     => Tweak.Description;
    public string  Category        => Tweak.Category;

    [ObservableProperty] private bool _isEnabled;

    public TelemetryTweakViewModel(TelemetryTweak tweak, DebloaterViewModel parent)
    {
        Tweak    = tweak;
        _parent  = parent;
        _isEnabled = tweak.IsCurrentlyEnabled;
    }

    partial void OnIsEnabledChanging(bool value)
    {
        // Only trigger if actually changed by user toggle, not programmatic set
        if (value != _isEnabled)
            _ = _parent.ApplyTweakAsync(this, value);
    }
}
