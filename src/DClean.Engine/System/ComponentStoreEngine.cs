using DClean.Contracts;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace DClean.Engine.OS;

[SupportedOSPlatform("windows")]
public sealed class ComponentStoreEngine(ILogger<ComponentStoreEngine> log)
{
    // ── DISM AnalyzeComponentStore ────────────────────────────────────

    public async Task<ComponentStoreAnalysis> AnalyzeAsync(CancellationToken ct = default)
    {
        log.LogInformation("Running DISM AnalyzeComponentStore...");
        string output = await RunCaptureAsync("dism.exe",
            "/Online /Cleanup-Image /AnalyzeComponentStore", ct).ConfigureAwait(false);
        return ParseAnalysis(output);
    }

    // ── DISM StartComponentCleanup (streaming progress) ──────────────

    public async IAsyncEnumerable<string> CleanupAsync(
        bool resetBase, [EnumeratorCancellation] CancellationToken ct = default)
    {
        string args = resetBase
            ? "/Online /Cleanup-Image /StartComponentCleanup /ResetBase"
            : "/Online /Cleanup-Image /StartComponentCleanup";

        log.LogInformation("Running DISM ComponentCleanup (resetBase={Reset})...", resetBase);
        yield return "⏳ מפעיל DISM — עשוי לקחת מספר דקות...";

        // Poll DISM log file while the process runs
        string logPath = @"C:\Windows\Logs\DISM\dism.log";
        int lastPct = 0;

        var captureTask = RunCaptureAsync("dism.exe", args, ct);

        while (!captureTask.IsCompleted)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            int pct = PollDismProgress(logPath, lastPct);
            if (pct > lastPct)
            {
                lastPct = pct;
                yield return $"⏳ {pct}% — מעבד חבילות...";
            }
        }

        string output = await captureTask;
        bool success = output.Contains("operation completed successfully",
            StringComparison.OrdinalIgnoreCase)
            || output.Contains("The operation completed successfully",
            StringComparison.OrdinalIgnoreCase);

        yield return success
            ? "✅ ניקוי Component Store הושלם"
            : $"⚠️ DISM סיים עם אזהרות:\n{output.Split('\n').LastOrDefault(l => l.Trim().Length > 0)}";
    }

    // ── SFC /scannow (streaming progress) ────────────────────────────

    public async IAsyncEnumerable<string> RunSfcAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        log.LogInformation("Running SFC /scannow...");
        yield return "🔍 מריץ בדיקת שלמות קבצי מערכת (SFC)...";
        yield return "⏳ הבדיקה עשויה לקחת מספר דקות...";

        string output = await RunCaptureAsync("sfc.exe", "/scannow", ct).ConfigureAwait(false);

        // Read CBS.log for details
        string cbsLog = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Logs", "CBS", "CBS.log");

        bool noProblem = output.Contains("did not find any integrity violations",
            StringComparison.OrdinalIgnoreCase);
        bool repaired  = output.Contains("successfully repaired",
            StringComparison.OrdinalIgnoreCase);
        bool problem   = output.Contains("found corrupt files",
            StringComparison.OrdinalIgnoreCase);

        if (noProblem)
            yield return "✅ לא נמצאו בעיות שלמות קבצים";
        else if (repaired)
            yield return "✅ נמצאו ותוקנו קבצים פגומים";
        else if (problem)
            yield return "⚠️ נמצאו קבצים פגומים — לא כולם תוקנו. בדוק CBS.log לפרטים.";
        else
            yield return $"ℹ️ {output.Split('\n').LastOrDefault(l => l.Trim().Length > 0)}";

        // Parse CBS.log for corrupt files — collect first, yield outside try-catch
        var cbsLines = new List<string>();
        if (File.Exists(cbsLog))
        {
            try
            {
                string cbsContent = await File.ReadAllTextAsync(cbsLog, ct).ConfigureAwait(false);
                var corrupt = Regex.Matches(cbsContent,
                    @"\[SR\] Found corrupt file\s+(.+)$", RegexOptions.Multiline);
                if (corrupt.Count > 0)
                {
                    cbsLines.Add($"📋 נמצאו {corrupt.Count} קבצים פגומים:");
                    foreach (Match m in corrupt.Cast<Match>().Take(10))
                        cbsLines.Add($"  ❌ {m.Groups[1].Value.Trim()}");
                }
            }
            catch (Exception ex) { log.LogDebug(ex, "CBS.log may be locked"); }
        }

        foreach (var line in cbsLines)
            yield return line;
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static async Task<string> RunCaptureAsync(
        string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };
        using var proc = Process.Start(psi)!;
        // Read stdout and stderr concurrently to prevent pipe-buffer deadlock
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return stdoutTask.Result;
    }

    private static ComponentStoreAnalysis ParseAnalysis(string output)
    {
        long ParseGb(string label)
        {
            var m = Regex.Match(output,
                label + @".*?:\s*([\d,\.]+)\s*GB", RegexOptions.IgnoreCase);
            if (m.Success && double.TryParse(
                m.Groups[1].Value.Replace(",", ""), out double gb))
                return (long)(gb * 1_073_741_824);
            return 0;
        }

        long storeSize   = ParseGb("Component Store Size");
        long reclaimable = ParseGb("Reclaimable Packages");

        var recLine = Regex.Match(output,
            @"Recommended Action\s*[:\-]+\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        string recommendation = recLine.Success
            ? recLine.Groups[1].Value.Trim() : "לא זמין";

        return new ComponentStoreAnalysis(storeSize, reclaimable, recommendation);
    }

    private int PollDismProgress(string logPath, int lastPct)
    {
        try
        {
            if (!File.Exists(logPath)) return lastPct;
            string tail = ReadTail(logPath, 4096);
            var m = Regex.Match(tail, @"(\d+\.\d+)% complete");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double pct))
                return Math.Max(lastPct, (int)pct);
        }
        catch (Exception ex) { log.LogDebug(ex, "Error polling DISM progress"); }
        return lastPct;
    }

    private static string ReadTail(string path, int bytes)
    {
        using var fs     = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long start       = Math.Max(0, fs.Length - bytes);
        fs.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }
}
