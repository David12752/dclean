namespace DClean.Contracts;

/// <summary>
/// Shared formatting utilities used across ViewModels and services.
/// </summary>
public static class FormatHelper
{
    /// <summary>
    /// Formats a byte count into a human-readable string (B, KB, MB, GB, TB).
    /// Handles negative/zero values gracefully.
    /// </summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        <= 0            => "—",
        < 1024          => $"{bytes} B",
        < 1_048_576     => $"{bytes / 1024.0:F1} KB",
        < 1_073_741_824 => $"{bytes / 1_048_576.0:F1} MB",
        < 1_099_511_627_776L => $"{bytes / 1_073_741_824.0:F1} GB",
        _               => $"{bytes / 1_099_511_627_776.0:F2} TB",
    };
}
