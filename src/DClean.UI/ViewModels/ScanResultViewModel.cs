using DClean.Contracts;
using System.Windows.Media;

namespace DClean.UI.ViewModels;

public sealed class ScanResultViewModel
{
    public string DisplayPath    { get; }
    public string SizeDisplay    { get; }
    public string StatusLabel    { get; }
    public Brush  StatusBrush    { get; }
    public Brush  BadgeBrush     { get; }
    public string Category       { get; }
    public string CategoryIcon   { get; }
    public string FileName       { get; }
    public string FolderPath     { get; }

    public ScanResultViewModel(ScanResult result)
    {
        DisplayPath = result.Path;
        SizeDisplay = FormatHelper.FormatBytes(result.SizeBytes);

        // Split into folder + filename for better display
        FileName   = System.IO.Path.GetFileName(result.Path);
        FolderPath = System.IO.Path.GetDirectoryName(result.Path) ?? result.Path;

        // Determine category from path
        (Category, CategoryIcon) = ClassifyPath(result.Path);

        if (result.Blocked)
        {
            StatusLabel = "חסום";
            StatusBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x99));
            BadgeBrush  = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x80));
        }
        else
        {
            StatusLabel = "נמצא";
            StatusBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB9, 0x00));
            BadgeBrush  = new SolidColorBrush(Color.FromRgb(0x80, 0x5A, 0x00));
        }
    }

    private static (string category, string icon) ClassifyPath(string path)
    {
        var lower = path.ToLowerInvariant();

        if (lower.Contains("chrome")  || lower.Contains("chromium") ||
            lower.Contains("firefox") || lower.Contains("\\edge\\")  ||
            lower.Contains("opera")   || lower.Contains("\\brave\\") ||
            lower.Contains("\\vivaldi\\"))
            return ("מטמון דפדפן", "🌐");

        if (lower.Contains("\\prefetch\\") || lower.EndsWith(".pf"))
            return ("Prefetch", "⚡");

        if (lower.Contains("\\thumbnails\\") || lower.Contains("thumbs.db") ||
            lower.Contains("thumbcache"))
            return ("תמונות ממוזערות", "🖼️");

        if (lower.Contains("crash") || lower.EndsWith(".dmp") ||
            lower.Contains("\\minidump\\") || lower.Contains("\\crashreports\\"))
            return ("קבצי קריסה", "💥");

        if (lower.Contains("\\log\\")  || lower.Contains("\\logs\\") ||
            lower.EndsWith(".log")     || lower.EndsWith(".etl"))
            return ("קבצי יומן", "📋");

        if (lower.Contains("delivery optimization") || lower.Contains("\\softwaredistribution\\"))
            return ("עדכוני Windows", "🪟");

        if (lower.Contains("\\temp\\") || lower.Contains("\\tmp\\") ||
            lower.EndsWith(".tmp")     || lower.Contains("\\temporary"))
            return ("קבצים זמניים", "🗑️");

        if (lower.Contains("\\recycle") || lower.Contains("$recycle"))
            return ("סל מיחזור", "♻️");

        return ("קבצים אחרים", "📄");
    }
}
