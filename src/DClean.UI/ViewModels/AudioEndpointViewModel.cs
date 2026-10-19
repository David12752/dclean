using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DClean.Contracts;
using DClean.UI.Services;
using System.Collections.ObjectModel;

namespace DClean.UI.ViewModels;

public sealed partial class AudioEndpointViewModel : ObservableObject
{
    private readonly IPipeClient _client;

    [ObservableProperty] private bool   _isBusy      = false;
    [ObservableProperty] private string _statusText  = "לחץ 'טען' לצפייה בנקודות הקצה של השמע";
    [ObservableProperty] private string _selectedDevice = "microphone";

    public ObservableCollection<AudioEndpointInfo> Endpoints    { get; } = [];
    public ObservableCollection<PrivacyAppEntry>   MicApps      { get; } = [];
    public ObservableCollection<PrivacyAppEntry>   CameraApps   { get; } = [];

    public AudioEndpointViewModel(IPipeClient client) => _client = client;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task LoadAsync()
    {
        IsBusy     = true;
        StatusText = "טוען נקודות קצה...";
        Endpoints.Clear();
        MicApps.Clear();
        CameraApps.Clear();
        try
        {
            var endpoints = await _client.GetAudioEndpointsAsync();
            foreach (var e in endpoints) Endpoints.Add(e);

            var mic    = await _client.GetPrivacyEntriesAsync("microphone");
            var camera = await _client.GetPrivacyEntriesAsync("webcam");
            foreach (var a in mic)    MicApps.Add(a);
            foreach (var a in camera) CameraApps.Add(a);

            int locked = Endpoints.Count(e => e.IsExclusiveLocked);
            StatusText = $"{Endpoints.Count} נקודות קצה, {locked} נעולות בבלעדיות";
        }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task RestartAudioAsync()
    {
        IsBusy     = true;
        StatusText = "מאתחל שירותי שמע...";
        try
        {
            bool ok    = await _client.RestartAudioServiceAsync();
            StatusText = ok
                ? "✅ שירותי השמע אותחלו בהצלחה"
                : "⚠️ האיחול נכשל — ייתכן שנדרשות הרשאות מנהל";
            if (ok) await LoadAsync();
        }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task BlockAccessAsync(PrivacyAppEntry app)
    {
        IsBusy = true;
        try
        {
            await _client.SetPrivacyAccessAsync(app.Device, app.AppName, false);
            await LoadAsync();
        }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task AllowAccessAsync(PrivacyAppEntry app)
    {
        IsBusy = true;
        try
        {
            await _client.SetPrivacyAccessAsync(app.Device, app.AppName, true);
            await LoadAsync();
        }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private bool CanAct() => !IsBusy;
}
