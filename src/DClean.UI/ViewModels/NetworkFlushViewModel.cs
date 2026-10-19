using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DClean.Contracts;
using DClean.UI.Services;
using System.Collections.ObjectModel;

namespace DClean.UI.ViewModels;

public sealed partial class NetworkFlushViewModel : ObservableObject
{
    private readonly IPipeClient _client;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool   _isFlushing      = false;
    [ObservableProperty] private string _statusText      = "בחר פעולות ולחץ 'בצע איפוס'";
    [ObservableProperty] private bool   _requiresReboot  = false;

    [ObservableProperty] private bool _flushDns          = true;
    [ObservableProperty] private bool _releaseRenewDhcp  = false;
    [ObservableProperty] private bool _flushArp          = true;
    [ObservableProperty] private bool _resetWinsock      = false;
    [ObservableProperty] private bool _resetTcpIp        = false;

    public ObservableCollection<NetworkFlushStep> Steps { get; } = [];

    public NetworkFlushViewModel(IPipeClient client) => _client = client;

    [RelayCommand(CanExecute = nameof(CanFlush))]
    private async Task FlushAsync()
    {
        IsFlushing    = true;
        RequiresReboot= false;
        StatusText    = "מבצע איפוס...";
        Steps.Clear();
        _cts = new CancellationTokenSource();

        try
        {
            var req = new NetworkFlushRequest(
                FlushDns, ReleaseRenewDhcp, FlushArp, ResetWinsock, ResetTcpIp);

            await foreach (var step in _client.FlushNetworkAsync(req, _cts.Token))
            {
                Steps.Add(step);
                if (step.RequiresReboot) RequiresReboot = true;
                StatusText = step.Detail;
            }

            StatusText = RequiresReboot
                ? "✅ הושלם — נדרשת הפעלה מחדש"
                : "✅ איפוס הרשת הושלם";
        }
        catch (OperationCanceledException) { StatusText = "בוטל"; }
        catch (Exception ex)              { StatusText = $"שגיאה: {ex.Message}"; }
        finally
        {
            IsFlushing = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanFlush() => !IsFlushing &&
        (FlushDns || ReleaseRenewDhcp || FlushArp || ResetWinsock || ResetTcpIp);

    [RelayCommand(CanExecute = nameof(IsFlushing))]
    private void Cancel() => _cts?.Cancel();
}
