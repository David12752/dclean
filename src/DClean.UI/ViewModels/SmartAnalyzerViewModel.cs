using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DClean.Contracts;
using DClean.UI.Services;
using System.Collections.ObjectModel;

namespace DClean.UI.ViewModels;

public sealed partial class SmartAnalyzerViewModel : ObservableObject
{
    private const int AttentionHealthThreshold = 75;
    private const int HighTemperatureThreshold = 55;

    private readonly IPipeClient _client;

    [ObservableProperty] private bool        _isLoading    = false;
    [ObservableProperty] private string      _statusText   = "לחץ 'נתח' לקריאת מצב הכוננים";
    [ObservableProperty] private DriveHealth? _selectedDrive = null;

    public ObservableCollection<DriveHealth>          Drives     { get; } = [];
    public ObservableCollection<SmartAttributeEntry>  Attributes { get; } = [];

    public int AttentionDriveCount => Drives.Count(RequiresAttention);
    public int HealthyDriveCount => Math.Max(0, Drives.Count - AttentionDriveCount);
    public int CriticalAttributeCount => Attributes.Count(a => a.IsCritical);
    public int FailedAttributeCount => Attributes.Count(a => !a.IsHealthy);
    public bool HasSelectedDriveAttention => SelectedDrive is not null &&
        (RequiresAttention(SelectedDrive) || FailedAttributeCount > 0);

    public string SelectedDriveAttentionText => SelectedDrive switch
    {
        null => string.Empty,
        _ when FailedAttributeCount > 0 => $"{FailedAttributeCount} מאפייני S.M.A.R.T. חורגים מהערכים התקינים.",
        _ when SelectedDrive.HealthScore < AttentionHealthThreshold => "בריאות הכונן אינה אופטימלית. מומלץ לגבות ולעקוב.",
        _ when SelectedDrive.TemperatureCelsius >= HighTemperatureThreshold => "טמפרטורת הכונן גבוהה. יש לבדוק אוורור ועומס עבודה.",
        _ when !SelectedDrive.TrimEnabled => "TRIM כבוי. מומלץ להפעיל לשמירה על ביצועי SSD.",
        _ => ""
    };

    public SmartAnalyzerViewModel(IPipeClient client) => _client = client;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task AnalyzeAsync()
    {
        if (!await _client.EnsureConnectedAsync())
        {
            StatusText = "לא ניתן להתחבר לשירות";
            return;
        }
        IsLoading  = true;
        StatusText = "קורא נתוני S.M.A.R.T...";
        Drives.Clear();
        Attributes.Clear();
        SelectedDrive = null;
        try
        {
            var drives = (await _client.GetDriveHealthAsync())
                .OrderBy(d => d.HealthScore)
                .ThenByDescending(d => d.TemperatureCelsius)
                .ToList();
            foreach (var d in drives) Drives.Add(d);

            if (Drives.Count > 0)
            {
                SelectedDrive = Drives.FirstOrDefault(RequiresAttention) ?? Drives[0];
                ShowAttributes(SelectedDrive);
            }

            StatusText = Drives.Count > 0
                ? AttentionDriveCount > 0
                    ? $"נמצאו {Drives.Count} כוננים · {AttentionDriveCount} דורשים תשומת לב"
                    : $"נמצאו {Drives.Count} כוננים · ללא אינדיקציות חריגות"
                : "לא נמצאו כוננים — ייתכן שנדרשות הרשאות מנהל";

            NotifyDerivedStateChanged();
        }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task EnableTrimAsync()
    {
        if (!await _client.EnsureConnectedAsync())
        {
            StatusText = "לא ניתן להתחבר לשירות";
            return;
        }
        IsLoading  = true;
        StatusText = "מפעיל TRIM...";
        try
        {
            bool ok    = await _client.EnableTrimAsync();
            StatusText = ok ? "✅ TRIM הופעל בהצלחה" : "⚠️ הפעלת TRIM נכשלה";
            await AnalyzeAsync();
        }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private void SelectDrive(DriveHealth? drive)
    {
        if (drive == null) return;
        SelectedDrive = drive;
        ShowAttributes(drive);
    }

    partial void OnSelectedDriveChanged(DriveHealth? value)
    {
        if (value != null) ShowAttributes(value);
    }

    private void ShowAttributes(DriveHealth drive)
    {
        Attributes.Clear();
        foreach (var a in drive.Attributes) Attributes.Add(a);
        NotifyDerivedStateChanged();
    }

    private bool CanAct() => !IsLoading;

    public static string HealthLabel(int score) => score switch
    {
        >= 90 => "מצוין",
        >= 70 => "טוב",
        >= 50 => "סביר",
        >= 30 => "נמוך",
        _     => "קריטי"
    };

    public static string HealthColor(int score) => score switch
    {
        >= 90 => "#107C10",
        >= 70 => "#5BA300",
        >= 50 => "#FFB900",
        >= 30 => "#D13438",
        _     => "#8B0000"
    };

    private static bool RequiresAttention(DriveHealth drive) =>
        drive.HealthScore < AttentionHealthThreshold ||
        drive.TemperatureCelsius >= HighTemperatureThreshold ||
        !drive.TrimEnabled;

    private void NotifyDerivedStateChanged()
    {
        OnPropertyChanged(nameof(AttentionDriveCount));
        OnPropertyChanged(nameof(HealthyDriveCount));
        OnPropertyChanged(nameof(CriticalAttributeCount));
        OnPropertyChanged(nameof(FailedAttributeCount));
        OnPropertyChanged(nameof(HasSelectedDriveAttention));
        OnPropertyChanged(nameof(SelectedDriveAttentionText));
    }
}
