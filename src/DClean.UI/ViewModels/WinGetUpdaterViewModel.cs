using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DClean.Contracts;
using DClean.UI.Services;
using System.Collections.ObjectModel;

namespace DClean.UI.ViewModels;

public sealed partial class WinGetUpdaterViewModel : ObservableObject
{
    private readonly IPipeClient _client;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool   _isLoading   = false;
    [ObservableProperty] private bool   _isUpgrading = false;
    [ObservableProperty] private string _statusText  = "לחץ 'בדוק עדכונים'";
    [ObservableProperty] private int    _progress    = 0;

    public ObservableCollection<WinGetPackageViewModel> Packages { get; } = [];
    public ObservableCollection<string>                 Log      { get; } = [];

    public WinGetUpdaterViewModel(IPipeClient client)
    {
        _client = client;
    }

    [RelayCommand]
    public async Task CheckUpdatesAsync()
    {
        if (!await _client.EnsureConnectedAsync()) { StatusText = "לא ניתן להתחבר לשירות"; return; }
        IsLoading = true;
        Packages.Clear();
        Log.Clear();
        StatusText = "בודק עדכונים...";
        Progress   = 0;

        try
        {
            var outdated = await _client.GetOutdatedPackagesAsync();
            foreach (var p in outdated)
                Packages.Add(new WinGetPackageViewModel(p));

            StatusText = Packages.Count > 0
                ? $"נמצאו {Packages.Count} עדכונים זמינים"
                : "כל החבילות מעודכנות ✅";
            Progress = 100;
        }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanUpgrade))]
    private async Task UpgradeSelectedAsync()
    {
        var selected = Packages.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0) return;

        await RunUpgradeAsync(selected.Select(p => p.Id).ToList(), upgradeAll: false);
    }

    [RelayCommand(CanExecute = nameof(CanUpgrade))]
    private async Task UpgradeAllAsync()
    {
        await RunUpgradeAsync([], upgradeAll: true);
    }

    [RelayCommand]
    private void CancelUpgrade() => _cts?.Cancel();

    private bool CanUpgrade() => !IsUpgrading;

    private async Task RunUpgradeAsync(IReadOnlyList<string> ids, bool upgradeAll)
    {
        _cts        = new CancellationTokenSource();
        IsUpgrading = true;
        Log.Clear();
        Progress    = 0;

        try
        {
            if (upgradeAll)
            {
                StatusText = "מעדכן את כל החבילות...";
                await ConsumeProgress(string.Empty, upgradeAll: true);
            }
            else
            {
                int done = 0;
                foreach (string id in ids)
                {
                    StatusText = $"מעדכן {id} ({done + 1}/{ids.Count})...";
                    await ConsumeProgress(id, upgradeAll: false);
                    done++;
                    Progress = (int)((float)done / ids.Count * 100);
                }
            }

            StatusText = "עדכון הושלם ✅";
            Progress   = 100;
            // Refresh
            await CheckUpdatesAsync();
        }
        catch (OperationCanceledException) { StatusText = "עדכון בוטל"; }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; }
        finally
        {
            IsUpgrading = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task ConsumeProgress(string id, bool upgradeAll)
    {
        await foreach (string line in _client.UpgradePackageAsync(id, upgradeAll, _cts!.Token))
        {
            if (!string.IsNullOrWhiteSpace(line))
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    Log.Add(line);
                    if (Log.Count > 200) Log.RemoveAt(0);
                });
        }
    }

    [RelayCommand]
    private void SelectAllPackages()
    {
        foreach (var p in Packages) p.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAllPackages()
    {
        foreach (var p in Packages) p.IsSelected = false;
    }
}

public sealed partial class WinGetPackageViewModel(WinGetPackage pkg) : ObservableObject
{
    public WinGetPackage Package          => pkg;
    public string Name                    => pkg.Name;
    public string Id                      => pkg.Id;
    public string InstalledVersion        => pkg.InstalledVersion;
    public string AvailableVersion        => pkg.AvailableVersion;
    public string Source                  => pkg.Source;

    [ObservableProperty] private bool _isSelected = true;
}
