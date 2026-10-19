using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Media;

namespace DClean.UI.ViewModels;

public sealed record LogEntry(string Timestamp, string Level, string Message)
{
    public Brush LevelBrush => Level switch
    {
        "CRIT"  => new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)),
        "WARN"  => new SolidColorBrush(Color.FromRgb(0xFF, 0xB9, 0x00)),
        "INFO"  => new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10)),
        _       => new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x80)),
    };
}

public sealed partial class LogViewModel : ObservableObject
{
    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    public int EntryCount => LogEntries.Count;

    [ObservableProperty] private string _exportStatus = string.Empty;

    public void Append(string level, string message)
    {
        LogEntries.Insert(0, new LogEntry(
            Timestamp: DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Level:     level,
            Message:   message));
        OnPropertyChanged(nameof(EntryCount));
    }

    [RelayCommand]
    private void Clear()
    {
        LogEntries.Clear();
        OnPropertyChanged(nameof(EntryCount));
    }

    [RelayCommand]
    private void Export()
    {
        try
        {
            if (LogEntries.Count == 0)
            {
                ExportStatus = "אין רשומות לייצוא";
                return;
            }

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "DClean_Logs");
            Directory.CreateDirectory(dir);

            string ts   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(dir, $"dclean_log_{ts}.csv");

            var sb = new StringBuilder();
            sb.AppendLine("Timestamp,Level,Message");
            foreach (var entry in LogEntries)
            {
                string escaped = entry.Message.Replace("\"", "\"\"");
                sb.AppendLine($"\"{entry.Timestamp}\",\"{entry.Level}\",\"{escaped}\"");
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            ExportStatus = $"יוצא ל-{path}";
        }
        catch (Exception ex)
        {
            ExportStatus = $"שגיאת ייצוא: {ex.Message}";
        }
    }
}
