using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DClean.Contracts;
using DClean.UI.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace DClean.UI.ViewModels;

public sealed partial class ContextMenuViewModel : ObservableObject
{
    private readonly IPipeClient _client;

    [ObservableProperty] private bool   _isLoading  = false;
    [ObservableProperty] private bool   _isBusy     = false;
    [ObservableProperty] private string _statusText = "לחץ 'טען' לטעינת ערכי תפריט ההקשר";
    [ObservableProperty] private string _filter     = string.Empty;

    private IReadOnlyList<ContextMenuEntryViewModel> _allEntries = [];

    public ObservableCollection<ContextMenuEntryViewModel> Entries { get; } = [];

    public ContextMenuViewModel(IPipeClient client)
    {
        _client = client;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (!await _client.EnsureConnectedAsync()) { StatusText = "לא ניתן להתחבר לשירות"; return; }
        IsLoading = true;
        Entries.Clear();
        _allEntries = [];
        try
        {
            var raw = await _client.GetContextMenuEntriesAsync();
            _allEntries = raw.Select(e => new ContextMenuEntryViewModel(e, this)).ToList();
            ApplyFilter();
            StatusText = $"נטענו {raw.Count} ערכים";
        }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Entries.Clear();
        string f = Filter.Trim().ToLowerInvariant();
        var filtered = string.IsNullOrEmpty(f)
            ? _allEntries
            : _allEntries.Where(e =>
                e.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                e.TypeLabel.Contains(f, StringComparison.OrdinalIgnoreCase));
        foreach (var e in filtered) Entries.Add(e);
    }

    internal async Task ToggleEnabledAsync(ContextMenuEntryViewModel vm, bool enable)
    {
        IsBusy     = true;
        StatusText = $"{(enable ? "מפעיל" : "מכבה")}: {vm.DisplayName}";
        bool ok = await _client.SetContextMenuEnabledAsync(vm.Entry, enable);
        if (ok) vm.IsEnabled = enable;
        StatusText = ok ? $"{'✅'} {vm.DisplayName}" : $"{'❌'} כשלון: {vm.DisplayName}";
        IsBusy = false;
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task DeleteSelectedAsync()
    {
        var selected = Entries.Where(e => e.IsSelected).ToList();
        if (selected.Count == 0) return;

        var confirm = MessageBox.Show(
            $"למחוק {selected.Count} ערכי תפריט?\nפעולה זו בלתי הפיכה.",
            "אישור מחיקה", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        IsBusy = true;
        int ok = 0, fail = 0;
        foreach (var vm in selected)
        {
            bool success = await _client.DeleteContextMenuEntryAsync(vm.Entry);
            if (success) { Entries.Remove(vm); ok++; }
            else fail++;
        }
        StatusText = $"נמחקו {ok} ערכים{(fail > 0 ? $" · נכשלו {fail}" : string.Empty)}";
        IsBusy = false;
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var entry in Entries)
            entry.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var entry in Entries)
            entry.IsSelected = false;
    }

    private bool CanAct() => !IsBusy;
}

public sealed partial class ContextMenuEntryViewModel : ObservableObject
{
    private readonly ContextMenuViewModel _parent;

    public ContextMenuEntry Entry      { get; }
    public string DisplayName          => Entry.DisplayName;
    public string TypeLabel            => Entry.Type.ToString();
    public string? Command             => Entry.Command;
    public string RegistryPath         => Entry.RegistryPath;

    public Brush TypeBrush => Entry.Type switch
    {
        ContextMenuEntryType.FileShell      or
        ContextMenuEntryType.FileShellEx    => new SolidColorBrush(Color.FromRgb(0x00, 0xD4, 0xA0)),
        ContextMenuEntryType.FolderShell    or
        ContextMenuEntryType.FolderShellEx  => new SolidColorBrush(Color.FromRgb(0xFF, 0xAA, 0x00)),
        ContextMenuEntryType.SendTo         => new SolidColorBrush(Color.FromRgb(0x78, 0x99, 0xFF)),
        _                                   => new SolidColorBrush(Color.FromRgb(0x5A, 0x64, 0x78)),
    };

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _isSelected = false;

    public ContextMenuEntryViewModel(ContextMenuEntry entry, ContextMenuViewModel parent)
    {
        Entry    = entry;
        _parent  = parent;
        _isEnabled = entry.IsEnabled;
    }

    partial void OnIsEnabledChanging(bool value)
    {
        if (value != _isEnabled)
            _ = _parent.ToggleEnabledAsync(this, value);
    }
}
