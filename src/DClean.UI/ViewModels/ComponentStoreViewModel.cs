using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DClean.Contracts;
using DClean.UI.Services;
using System.Collections.ObjectModel;
using System.Windows;

namespace DClean.UI.ViewModels;

public sealed partial class ComponentStoreViewModel : ObservableObject
{
    private readonly IPipeClient _client;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool   _isBusy          = false;
    [ObservableProperty] private string _statusText      = "לחץ 'נתח' לבדיקת גודל WinSxS Component Store";
    [ObservableProperty] private long   _storeSizeBytes  = 0;
    [ObservableProperty] private long   _reclaimableBytes= 0;
    [ObservableProperty] private string _recommendation  = "";
    [ObservableProperty] private bool   _resetBase       = false;

    public ObservableCollection<string> Log { get; } = [];

    public ComponentStoreViewModel(IPipeClient client) => _client = client;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task AnalyzeAsync()
    {
        IsBusy     = true;
        StatusText = "מנתח Component Store...";
        Log.Clear();
        Log.Add("⏳ מריץ DISM AnalyzeComponentStore...");
        try
        {
            var result = await _client.AnalyzeComponentStoreAsync();
            if (result != null)
            {
                StoreSizeBytes   = result.StoreSizeBytes;
                ReclaimableBytes = result.ReclaimableBytes;
                Recommendation   = result.Recommendation;
                Log.Add($"📦 גודל Component Store: {FormatHelper.FormatBytes(StoreSizeBytes)}");
                Log.Add($"♻️  ניתן לפנות: {FormatHelper.FormatBytes(ReclaimableBytes)}");
                Log.Add($"💡 המלצה: {Recommendation}");
                StatusText = $"ניתן לפנות {FormatHelper.FormatBytes(ReclaimableBytes)}";
            }
            else
            {
                StatusText = "לא ניתן לקרוא נתוני DISM";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"שגיאה: {ex.Message}";
            Log.Add($"❌ {ex.Message}");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task CleanupAsync()
    {
        var confirm = MessageBox.Show(
            $"פעולה זו תנקה חבילות ישנות מ-Component Store.{(ResetBase ? "\n\n⚠️ ResetBase: לא ניתן לבטל עדכוני Windows לאחר פעולה זו!" : "")}",
            "אישור ניקוי WinSxS",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        IsBusy = true;
        Log.Clear();
        StatusText = "מנקה...";
        _cts = new CancellationTokenSource();
        try
        {
            await foreach (var line in _client.RunDismCleanupAsync(ResetBase, _cts.Token))
            {
                Log.Add(line);
                StatusText = line;
            }
        }
        catch (OperationCanceledException) { StatusText = "בוטל"; }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; Log.Add($"❌ {ex.Message}"); }
        finally { IsBusy = false; _cts?.Dispose(); _cts = null; }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task RunSfcAsync()
    {
        IsBusy = true;
        Log.Clear();
        StatusText = "מריץ SFC...";
        _cts = new CancellationTokenSource();
        try
        {
            await foreach (var line in _client.RunSfcAsync(_cts.Token))
            {
                Log.Add(line);
                StatusText = line;
            }
        }
        catch (OperationCanceledException) { StatusText = "בוטל"; }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; Log.Add($"❌ {ex.Message}"); }
        finally { IsBusy = false; _cts?.Dispose(); _cts = null; }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cts?.Cancel();

    private bool CanAct() => !IsBusy;
}
