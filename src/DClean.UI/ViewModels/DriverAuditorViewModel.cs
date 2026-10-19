using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DClean.Contracts;
using DClean.UI.Services;
using System.Collections.ObjectModel;

namespace DClean.UI.ViewModels;

public sealed partial class DriverAuditorViewModel : ObservableObject
{
    private readonly IPipeClient _client;

    [ObservableProperty] private bool   _isLoading    = false;
    [ObservableProperty] private string _statusText   = "לחץ 'סרוק' כדי לאחזר רשימת דרייברים";
    [ObservableProperty] private string _filterText   = "";
    [ObservableProperty] private bool   _showUnsigned = false;

    public ObservableCollection<DriverInfo> AllDrivers      { get; } = [];
    public ObservableCollection<DriverInfo> FilteredDrivers { get; } = [];

    public DriverAuditorViewModel(IPipeClient client) => _client = client;

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadAsync()
    {
        IsLoading  = true;
        StatusText = "מאחזר דרייברים...";
        AllDrivers.Clear();
        FilteredDrivers.Clear();
        try
        {
            var drivers = await _client.GetDriversAsync();
            foreach (var d in drivers) AllDrivers.Add(d);
            ApplyFilter();
            StatusText = $"נמצאו {AllDrivers.Count} דרייברים";
        }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    private bool CanLoad() => !IsLoading;

    [RelayCommand]
    private async Task ExportAsync()
    {
        IsLoading  = true;
        StatusText = "מייצא דרייברים (DISM)...";
        try
        {
            var result = await _client.ExportDriversAsync();
            StatusText = result?.Success == true
                ? $"✅ הייצוא הושלם: {result.ExportPath}"
                : "⚠️ הייצוא נכשל";
        }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    partial void OnFilterTextChanged(string value)   => ApplyFilter();
    partial void OnShowUnsignedChanged(bool value)    => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredDrivers.Clear();
        foreach (var d in AllDrivers)
        {
            bool matchFilter  = string.IsNullOrWhiteSpace(FilterText)
                || d.DeviceName.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
                || d.Provider.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
            bool matchSigned  = !ShowUnsigned || !d.IsSigned;
            if (matchFilter && matchSigned) FilteredDrivers.Add(d);
        }
        StatusText = FilteredDrivers.Count == AllDrivers.Count
            ? $"נמצאו {AllDrivers.Count} דרייברים"
            : $"מציג {FilteredDrivers.Count} מתוך {AllDrivers.Count}";
    }
}
