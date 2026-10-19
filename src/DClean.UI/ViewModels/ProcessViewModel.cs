using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DClean.Contracts;
using DClean.UI.Services;
using System.Collections.ObjectModel;
using System.Windows;

namespace DClean.UI.ViewModels;

public sealed partial class ProcessViewModel : ObservableObject
{
    private readonly IPipeClient _client;

    [ObservableProperty] private bool   _isLoading   = false;
    [ObservableProperty] private string _statusText  = "";
    [ObservableProperty] private ProcessItemViewModel? _selectedProcess;

    public ObservableCollection<ProcessItemViewModel> Processes { get; } = [];

    public ProcessViewModel(IPipeClient client) => _client = client;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!await _client.EnsureConnectedAsync())
        {
            StatusText = "לא ניתן להתחבר לשירות";
            return;
        }

        IsLoading  = true;
        StatusText = "טוען תהליכים...";
        Processes.Clear();

        try
        {
            var list = await _client.GetProcessesAsync();
            foreach (var p in list)
                Processes.Add(new ProcessItemViewModel(p));

            StatusText = $"נטענו {list.Count} תהליכים";
        }
        catch (Exception ex)
        {
            StatusText = $"שגיאה: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelected))]
    private async Task TrimSelectedAsync()
    {
        if (SelectedProcess == null) return;
        StatusText = $"מפנה זיכרון עבור {SelectedProcess.Name}...";
        try
        {
            var result = await _client.TrimAsync(SelectedProcess.Info);
            StatusText = result?.Success == true
                ? $"זיכרון שוחרר עבור {SelectedProcess.Name}"
                : $"נכשל: {result?.Detail}";
        }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(CanTerminate))]
    private async Task TerminateSelectedAsync()
    {
        if (SelectedProcess == null) return;

        var confirm = MessageBox.Show(
            $"לסיים את התהליך '{SelectedProcess.Name}' (PID {SelectedProcess.Pid})?\n\nנסיון יציאה מסודרת תחילה.",
            "סיום תהליך",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes) return;

        StatusText = $"מסיים {SelectedProcess.Name}...";
        try
        {
            var result = await _client.TerminateAsync(SelectedProcess.Info, force: false);
            StatusText = result?.Success == true
                ? $"התהליך {SelectedProcess.Name} הסתיים"
                : $"נכשל: {result?.Detail}";

            if (result?.Success == true)
                await RefreshAsync();
        }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; }
    }

    private bool CanActOnSelected()  => SelectedProcess != null && !SelectedProcess.IsCritical;
    private bool CanTerminate()      => SelectedProcess != null && !SelectedProcess.IsCritical && !SelectedProcess.IsProtected;
}

public sealed class ProcessItemViewModel
{
    public ProcessInfo Info         { get; }
    public int    Pid               => Info.Pid;
    public string Name              => Info.Name;
    public long   WorkingSetBytes   => Info.WorkingSetBytes;
    public string WorkingSetDisplay => FormatHelper.FormatBytes(Info.WorkingSetBytes);
    public bool   IsCritical        => Info.IsCritical;
    public bool   IsProtected       => Info.IsProtected;
    public string ProtectionLabel   =>
        IsCritical ? "קריטי" : IsProtected ? "מוגן" : "";

    public ProcessItemViewModel(ProcessInfo info) => Info = info;
}
