using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DClean.Contracts;
using DClean.UI.Services;
using System.Collections.ObjectModel;

namespace DClean.UI.ViewModels;

public sealed partial class DawCleanerViewModel : ObservableObject
{
    private readonly IPipeClient _client;

    [ObservableProperty] private bool   _isScanning   = false;
    [ObservableProperty] private string _statusText   = "לחץ 'סרוק' לאיתור cache של DAW ופלאגינים";
    [ObservableProperty] private long   _totalCacheBytes = 0;
    [ObservableProperty] private long   _freedBytes   = 0;

    public ObservableCollection<PluginEntry>         Plugins          { get; } = [];
    public ObservableCollection<DawCacheEntry>       Caches           { get; } = [];
    public ObservableCollection<DawCacheEntry>       SelectedCaches   { get; } = [];
    public ObservableCollection<BrokenSampleLibrary> BrokenLibraries  { get; } = [];

    public DawCleanerViewModel(IPipeClient client) => _client = client;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (!await _client.EnsureConnectedAsync())
        {
            StatusText = "לא ניתן להתחבר לשירות";
            return;
        }
        IsScanning = true;
        StatusText = "סורק...";
        Plugins.Clear();
        Caches.Clear();
        SelectedCaches.Clear();
        BrokenLibraries.Clear();
        TotalCacheBytes = 0;

        try
        {
            var result = await _client.ScanDawAsync();
            if (result == null) { StatusText = "לא נמצאו נתונים"; return; }

            foreach (var p in result.Plugins)  Plugins.Add(p);
            foreach (var c in result.Caches)
            {
                Caches.Add(c);
                TotalCacheBytes += c.SizeBytes;
            }
            foreach (var b in result.BrokenLibraries) BrokenLibraries.Add(b);

            StatusText = $"נמצאו {Plugins.Count} פלאגינים, " +
                         $"{Caches.Count} תיקיות cache ({FormatHelper.FormatBytes(TotalCacheBytes)}), " +
                         $"{BrokenLibraries.Count} ספריות שבורות";
        }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; }
        finally { IsScanning = false; }
    }

    private bool CanScan() => !IsScanning;

    [RelayCommand]
    private void SelectAllCaches()
    {
        SelectedCaches.Clear();
        foreach (var c in Caches) SelectedCaches.Add(c);
    }

    [RelayCommand]
    private void DeselectAllCaches() => SelectedCaches.Clear();

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedCaches.Count == 0) return;
        IsScanning = true;
        StatusText = "מוחק cache...";
        try
        {
            var paths = SelectedCaches.Select(c => c.CachePath).ToList();
            long freed = await _client.DeleteDawCachesAsync(paths);
            FreedBytes += freed;
            StatusText = $"✅ שוחרר {FormatHelper.FormatBytes(freed)}";
            await ScanAsync();
        }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; }
        finally { IsScanning = false; }
    }

    private bool CanDelete() => SelectedCaches.Count > 0 && !IsScanning;
}
