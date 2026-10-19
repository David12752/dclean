using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DClean.Contracts;
using DClean.UI.Services;

namespace DClean.UI.ViewModels;

public sealed partial class PerformanceTunerViewModel : ObservableObject
{
    private readonly IPipeClient _client;

    [ObservableProperty] private bool   _isLoading          = false;
    [ObservableProperty] private bool   _isBusy             = false;
    [ObservableProperty] private string _statusText         = "טוען פרופיל...";
    [ObservableProperty] private string _activePlanName     = "—";
    [ObservableProperty] private bool   _usbSuspendEnabled  = true;
    [ObservableProperty] private bool   _coreParkingEnabled = true;

    [ObservableProperty] private bool _applyUltimatePlan   = true;
    [ObservableProperty] private bool _disableUsbSuspend   = true;
    [ObservableProperty] private bool _disableCoreParking  = true;

    public PerformanceTunerViewModel(IPipeClient client)
    {
        _client = client;
    }

    [RelayCommand]
    public async Task LoadProfileAsync()
    {
        if (!await _client.EnsureConnectedAsync()) { StatusText = "לא ניתן להתחבר לשירות"; return; }
        IsLoading = true;
        try
        {
            var profile = await _client.GetPerformanceProfileAsync();
            if (profile != null)
            {
                ActivePlanName      = profile.ActivePlanName;
                UsbSuspendEnabled   = profile.UsbSelectiveSuspendEnabled;
                CoreParkingEnabled  = profile.CoreParkingEnabled;
                StatusText          = $"תוכנית פעילה: {profile.ActivePlanName}";
            }
        }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyTuningAsync()
    {
        IsBusy     = true;
        StatusText = "מחיל הגדרות ביצועים...";
        try
        {
            var req = new PerformanceTuneRequest(
                ApplyUltimatePlan:  ApplyUltimatePlan,
                DisableUsbSuspend:  DisableUsbSuspend,
                DisableCoreParking: DisableCoreParking);

            bool ok = await _client.ApplyPerformanceTuneAsync(req);
            StatusText = ok ? "✅ הגדרות הוחלו בהצלחה — מומלץ לאתחל" : "❌ חלק מהשינויים נכשלו";

            // Refresh
            await LoadProfileAsync();
        }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RestoreDefaultsAsync()
    {
        IsBusy     = true;
        StatusText = "משחזר הגדרות ברירת מחדל...";
        try
        {
            // Restore: Balanced plan, USB suspend ON, core parking ON
            var req = new PerformanceTuneRequest(
                ApplyUltimatePlan:  false,
                DisableUsbSuspend:  false,
                DisableCoreParking: false);
            await _client.ApplyPerformanceTuneAsync(req);
            StatusText = "הוחזרו הגדרות ברירת מחדל";
            await LoadProfileAsync();
        }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private bool CanApply() => !IsBusy;
}
