using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DClean.Contracts;
using DClean.UI.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;

namespace DClean.UI.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly IPipeClient _client;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string  _statusText        = "מוכן לסריקה";
    [ObservableProperty] private double  _scanProgress      = 0;
    [ObservableProperty] private bool    _isScanning        = false;
    [ObservableProperty] private bool    _isDryRun          = true;
    [ObservableProperty] private long    _totalBytesFound   = 0;
    [ObservableProperty] private long    _totalBytesFreed   = 0;
    [ObservableProperty] private string  _ramText           = "---";
    [ObservableProperty] private double  _ramPercent        = 0;
    [ObservableProperty] private bool    _isConnected        = false;
    [ObservableProperty] private string  _connectionStatus   = "מתחבר לשירות...";
    [ObservableProperty] private bool    _isServiceNotFound  = false;
    [ObservableProperty] private string  _serviceExePath     = "לא נמצא";
    [ObservableProperty] private bool    _isStartingService  = false;

    public ObservableCollection<ScanResultViewModel> ScanResults { get; } = [];

    public DashboardViewModel(IPipeClient client)
    {
        _client = client;
        _ = AutoConnectAsync();
    }

    private async Task AutoConnectAsync()
    {
        IsServiceNotFound  = false;
        IsStartingService  = false;

        // Phase 1: Try to connect — up to 3 attempts with 3s timeout each.
        // Handles the case where the service is already running (dev, Windows Service, dotnet run).
        for (int i = 1; i <= 3; i++)
        {
            ConnectionStatus = i == 1 ? "מתחבר לשירות..." : $"מנסה להתחבר ({i}/3)...";
            try
            {
                await _client.ConnectAsync(TimeSpan.FromSeconds(3));
                IsConnected      = true;
                ConnectionStatus = "מחובר ✓";
                StatusText       = "מחובר לשירות. מוכן לסריקה.";
                await RefreshRamAsync();
                return;
            }
            catch { /* not yet available */ }

            // If the pipe exists but connection timed out (server busy), wait and retry
            if (ServiceLauncher.IsPipeAvailable())
            {
                await Task.Delay(1500);
                continue;
            }
            break; // pipe doesn't exist at all — stop retrying and start service
        }

        // Phase 2: Service is not running — find and start it
        IsStartingService = true;
        ConnectionStatus  = "מאתחל שירות...";
        StatusText        = "מאתר ומפעיל את שירות DClean...";

        string? found = await ServiceLauncher.EnsureRunningAsync();
        IsStartingService = false;

        if (found == null)
        {
            IsServiceNotFound = true;
            ServiceExePath    = ServiceLauncher.FindServiceExe() ?? "לא נמצא";
            ConnectionStatus  = "שירות לא נמצא";
            StatusText        = "לא נמצא DClean.Service.exe — בנה את הפרויקט תחילה (dotnet build).";
            return;
        }

        // Phase 3: Retry after service start — up to 8 attempts
        for (int i = 1; i <= 8; i++)
        {
            ConnectionStatus = $"מחכה לשירות ({i}/8)...";
            try
            {
                await _client.ConnectAsync(TimeSpan.FromSeconds(3));
                IsConnected      = true;
                ConnectionStatus = "מחובר ✓";
                StatusText       = "מחובר לשירות. מוכן לסריקה.";
                await RefreshRamAsync();
                return;
            }
            catch { await Task.Delay(1000); }
        }

        ConnectionStatus = "שירות לא זמין";
        StatusText       = "השירות הופעל אך לא ניתן להתחבר. לחץ 'הפעל שירות' לנסיון חוזר.";
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        IsConnected = false;
        await AutoConnectAsync();
    }

    [RelayCommand]
    private async Task StartServiceAsync()
    {
        await ConnectAsync();
    }

    [RelayCommand]
    private void OpenServiceFolder()
    {
        string? path = ServiceLauncher.FindServiceExe();
        if (path != null)
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else
        {
            // Open the solution Service project bin folder as best guess
            string guess = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(AppContext.BaseDirectory,
                    "..", "..", "..", "..", "DClean.Service"));
            if (System.IO.Directory.Exists(guess))
                Process.Start("explorer.exe", guess);
        }
    }

    [RelayCommand]
    private async Task RefreshRamAsync()
    {
        if (!_client.IsConnected) return;
        try
        {
            var snap = await _client.GetRamSnapshotAsync();
            if (snap != null)
            {
                RamPercent = snap.UsedPercent;
                long used  = snap.TotalBytes - snap.AvailableBytes;
                RamText    = $"{FormatHelper.FormatBytes(used)} / {FormatHelper.FormatBytes(snap.TotalBytes)} ({snap.UsedPercent}%)";
            }
        }
        catch { /* non-critical */ }
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task StartScanAsync()
    {
        if (!_client.IsConnected)
        {
            await ConnectAsync();
            if (!_client.IsConnected) return;
        }

        ScanResults.Clear();
        TotalBytesFound = 0;
        TotalBytesFreed = 0;
        IsScanning      = true;
        ScanProgress    = 0;
        StatusText      = IsDryRun ? "סורק (מצב בדיקה)..." : "סורק וממחק...";

        _cts = new CancellationTokenSource();
        var deleteCandidates = new List<ScanResult>();

        var progress = new Progress<(ScanResult r, double pct)>(update =>
        {
            ScanResults.Add(new ScanResultViewModel(update.r));
            if (!update.r.Blocked)
            {
                TotalBytesFound += update.r.SizeBytes;
                deleteCandidates.Add(update.r);
            }
            ScanProgress = update.pct;
        });

        try
        {
            int count = 0;
            await foreach (var result in _client.ScanAsync(IsDryRun, ct: _cts.Token))
            {
                count++;
                ((IProgress<(ScanResult, double)>)progress).Report(
                    (result, Math.Min(99, count * 0.05)));
            }

            if (!IsDryRun && deleteCandidates.Count > 0)
            {
                int deleted = 0;
                foreach (var candidate in deleteCandidates)
                {
                    _cts.Token.ThrowIfCancellationRequested();

                    StatusText = $"מוחק {deleted + 1} מתוך {deleteCandidates.Count}...";
                    var outcome = await _client.DeleteAsync(candidate.Path, _cts.Token);
                    if (outcome is { Status: DeleteStatus.Deleted or DeleteStatus.ScheduledReboot })
                    {
                        TotalBytesFreed += outcome.BytesFreed;
                    }

                    deleted++;
                    ScanProgress = 99 + (deleted / (double)deleteCandidates.Count);
                }
            }

            ScanProgress = 100;
            StatusText = IsDryRun
                ? $"סריקת בדיקה הושלמה — נמצאו {FormatHelper.FormatBytes(TotalBytesFound)}"
                : $"ניקוי הושלם — שוחרר {FormatHelper.FormatBytes(TotalBytesFreed)}";

            await RefreshRamAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "הסריקה בוטלה";
        }
        catch (Exception ex)
        {
            StatusText = $"שגיאה: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private bool CanScan() => !IsScanning;

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void CancelScan() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteCleaningAsync()
    {
        var result = MessageBox.Show(
            $"האם למחוק {FormatHelper.FormatBytes(TotalBytesFound)} של קבצים?\n\nפעולה זו אינה הפיכה.",
            "אישור מחיקה",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes) return;

        // Request execution token FROM the service (service sets mode → AwaitingConfirmation)
        string? token = await _client.RequestExecutionAsync();
        if (string.IsNullOrEmpty(token))
        {
            StatusText = "שגיאה: לא ניתן לקבל אסימון ביצוע מהשירות.";
            return;
        }

        bool confirmed = await _client.ConfirmExecutionAsync(token);
        if (!confirmed)
        {
            StatusText = "אישור הביצוע נכשל. מצב הסריקה אופס.";
            return;
        }

        try
        {
            IsDryRun = false;
            await StartScanAsync();
        }
        finally
        {
            IsDryRun = true;
            await _client.ResetExecutionAsync();
        }
    }

    private bool CanExecute() => !IsScanning && TotalBytesFound > 0;
}
