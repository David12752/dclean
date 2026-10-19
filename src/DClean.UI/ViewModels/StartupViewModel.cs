using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DClean.Contracts;
using DClean.UI.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace DClean.UI.ViewModels;

public sealed partial class StartupViewModel : ObservableObject
{
    private readonly IPipeClient _client;

    [ObservableProperty] private bool   _isLoading   = false;
    [ObservableProperty] private bool   _isBusy      = false;
    [ObservableProperty] private string _statusText  = "טוען פריטי הפעלה...";
    [ObservableProperty] private string _filterText  = string.Empty;
    [ObservableProperty] private StartupItemViewModel? _selectedItem;

    public ObservableCollection<StartupItemViewModel> AllItems      { get; } = [];
    public ObservableCollection<StartupItemViewModel> FilteredItems { get; } = [];

    public StartupViewModel(IPipeClient client)
    {
        _client = client;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FilterText)) ApplyFilter();
        };
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (!await _client.EnsureConnectedAsync()) { StatusText = "לא ניתן להתחבר לשירות"; return; }
        IsLoading = true;
        AllItems.Clear();
        FilteredItems.Clear();

        try
        {
            var items = await _client.GetStartupItemsAsync();
            foreach (var item in items)
                AllItems.Add(new StartupItemViewModel(item));
            ApplyFilter();
            StatusText = $"{AllItems.Count} פריטי הפעלה";
        }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    private void ApplyFilter()
    {
        FilteredItems.Clear();
        string q = FilterText.Trim().ToLowerInvariant();
        var grouped = AllItems
            .Where(i => string.IsNullOrEmpty(q)
                || i.Name.ToLowerInvariant().Contains(q)
                || i.Publisher.ToLowerInvariant().Contains(q))
            .OrderBy(i => i.SourceGroup)
            .ThenBy(i => i.Name);
        foreach (var i in grouped) FilteredItems.Add(i);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DisableAsync()
    {
        if (SelectedItem is null) return;
        await SetStatusAsync(SelectedItem, StartupItemStatus.Disabled, 0);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EnableAsync()
    {
        if (SelectedItem is null) return;
        await SetStatusAsync(SelectedItem, StartupItemStatus.Enabled, 0);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DelayStartAsync()
    {
        if (SelectedItem is null) return;
        await SetStatusAsync(SelectedItem, StartupItemStatus.DelayedAutoStart,
            SelectedItem.DelayMinutes > 0 ? SelectedItem.DelayMinutes : 2);
    }

    private bool HasSelection() => SelectedItem != null && !IsBusy;

    private async Task SetStatusAsync(StartupItemViewModel vm, StartupItemStatus status, int delay)
    {
        IsBusy     = true;
        StatusText = $"מעדכן: {vm.Name}...";
        bool ok    = await _client.SetStartupStatusAsync(vm.Item, status, delay);
        if (ok)
        {
            vm.UpdateStatus(status, delay);
            StatusText = $"עודכן: {vm.Name}";
        }
        else StatusText = "פעולה נכשלה";
        IsBusy = false;
    }

    partial void OnSelectedItemChanged(StartupItemViewModel? value)
    {
        DisableCommand.NotifyCanExecuteChanged();
        EnableCommand.NotifyCanExecuteChanged();
        DelayStartCommand.NotifyCanExecuteChanged();
    }
}

public sealed partial class StartupItemViewModel(StartupItem item) : ObservableObject
{
    public StartupItem Item => item;

    public string Name        => item.Name;
    public string Publisher   => item.Publisher;
    public string Command     => item.Command;
    public string SourceLabel => item.Source switch
    {
        StartupItemSource.RegistryRunHklm      => "HKLM\\Run",
        StartupItemSource.RegistryRunHkcu      => "HKCU\\Run",
        StartupItemSource.RegistryRunOncHklm   => "HKLM\\RunOnce",
        StartupItemSource.RegistryRunOnceHkcu  => "HKCU\\RunOnce",
        StartupItemSource.StartupFolderUser    => "תיקיית הפעלה (משתמש)",
        StartupItemSource.StartupFolderCommon  => "תיקיית הפעלה (כולם)",
        StartupItemSource.ScheduledTask        => "משימה מתוזמנת",
        StartupItemSource.WindowsService       => "שירות Windows",
        _                                      => item.Source.ToString()
    };

    public string SourceGroup => item.Source switch
    {
        StartupItemSource.WindowsService  => "ב",
        StartupItemSource.ScheduledTask   => "א",
        _                                 => "ג"  // Registry/Folder come last
    };

    [ObservableProperty] private StartupItemStatus _status = item.Status;
    [ObservableProperty] private int _delayMinutes = item.DelayMinutes;

    public string StatusLabel => Status switch
    {
        StartupItemStatus.Enabled          => "פעיל",
        StartupItemStatus.Disabled         => "מושבת",
        StartupItemStatus.DelayedAutoStart => $"מאוחר {DelayMinutes} דק'",
        _                                  => Status.ToString()
    };

    public Brush StatusBrush => new SolidColorBrush((Color)ColorConverter.ConvertFromString(
        Status == StartupItemStatus.Enabled          ? "#00D4A0" :
        Status == StartupItemStatus.Disabled         ? "#FF4D5B" : "#F0A500"));

    public void UpdateStatus(StartupItemStatus newStatus, int delay)
    {
        Status       = newStatus;
        DelayMinutes = delay;
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusBrush));
    }
}
