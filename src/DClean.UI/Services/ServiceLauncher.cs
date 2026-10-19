using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DClean.UI.Services;

/// <summary>
/// Finds and launches the DClean.Service backend process when it is not already running.
/// Searches common paths relative to the running executable (dev + published layouts).
/// </summary>
public static class ServiceLauncher
{
    private const string ServiceExeName = "DClean.Service.exe";
    private const string PipeName       = "DCleanService";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool WaitNamedPipe(string lpNamedPipeName, uint nTimeOut);

    /// <summary>
    /// Returns true when the named pipe server is already listening
    /// (works whether service runs as exe, Windows Service, or 'dotnet run').
    /// </summary>
    public static bool IsPipeAvailable() =>
        WaitNamedPipe($@"\\.\pipe\{PipeName}", 0);

    /// <summary>
    /// Tries to find and start the service if it is not already running.
    /// Returns the path used, or null if not found.
    /// </summary>
    public static async Task<string?> EnsureRunningAsync()
    {
        if (IsPipeAvailable()) return "(already running)";

        string? exePath = FindServiceExe();
        if (exePath == null) return null;

        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute      = true,        // allows UAC prompt if needed
            WindowStyle          = ProcessWindowStyle.Hidden,
            CreateNoWindow       = true,
        };

        try
        {
            Process.Start(psi);
            // Give the service a moment to open its named pipe
            await Task.Delay(2000);
            return exePath;
        }
        catch
        {
            // Try without UAC (non-elevated) — many operations still work
            psi.UseShellExecute = false;
            try
            {
                Process.Start(psi);
                await Task.Delay(2000);
                return exePath;
            }
            catch { return null; }
        }
    }

    /// <summary>Locates DClean.Service.exe relative to the current executable.</summary>
    public static string? FindServiceExe()
    {
        string? baseDir = AppContext.BaseDirectory;
        if (baseDir == null) return null;

        // Possible locations (dev build / single-file publish)
        string[] candidates =
        [
            // Same folder (published single-folder layout)
            Path.Combine(baseDir, ServiceExeName),

            // Debug: sibling project bin
            Path.Combine(baseDir, "..", "..", "..", "..",
                "DClean.Service", "bin", "Debug", "net9.0-windows", ServiceExeName),

            // Release: sibling project bin
            Path.Combine(baseDir, "..", "..", "..", "..",
                "DClean.Service", "bin", "Release", "net9.0-windows", ServiceExeName),

            // One level up (e.g. if both are in the same output dir)
            Path.Combine(baseDir, "..", ServiceExeName),
        ];

        foreach (string c in candidates)
        {
            string full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }

        return null;
    }
}
