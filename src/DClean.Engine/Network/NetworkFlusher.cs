using DClean.Contracts;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DClean.Engine.Network;

[SupportedOSPlatform("windows")]
public sealed class NetworkFlusher(ILogger<NetworkFlusher> log)
{
    private const int DhcpRenewDelayMs = 600;

    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    private static extern bool DnsFlushResolverCache();

    public async IAsyncEnumerable<NetworkFlushStep> FlushAsync(
        bool flushDns, bool releaseRenewDhcp, bool flushArp,
        bool resetWinsock, bool resetTcpIp,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        bool requiresReboot = false;

        if (flushDns)
        {
            yield return await RunStepAsync("DNS Cache", "ניקוי DNS cache (Native API)...", ct,
                () =>
                {
                    bool ok = DnsFlushResolverCache();
                    return (ok, ok ? "" : "DnsFlushResolverCache returned false", false);
                });
        }

        if (releaseRenewDhcp)
        {
            yield return await RunStepAsync("DHCP Release/Renew", "שחרור וחידוש כתובת IP...", ct,
                async () =>
                {
                    string? ip = await RunNetshAsync("interface ip delete arpcache", ct).ConfigureAwait(false);
                    // ipconfig /release + /renew
                    await RunIpconfigAsync("release", ct).ConfigureAwait(false);
                    await Task.Delay(DhcpRenewDelayMs, ct).ConfigureAwait(false);
                    string? newIp = await RunIpconfigAsync("renew", ct).ConfigureAwait(false);
                    return (true, newIp?.Trim() ?? "", false);
                });
        }

        if (flushArp)
        {
            yield return await RunStepAsync("ARP Cache", "ניקוי ARP cache...", ct,
                async () =>
                {
                    string? output = await RunNetshAsync("interface ip delete arpcache", ct).ConfigureAwait(false);
                    return (true, output?.Trim() ?? "done", false);
                });
        }

        if (resetWinsock)
        {
            requiresReboot = true;
            yield return await RunStepAsync("Winsock Reset", "איפוס Winsock catalog...", ct,
                async () =>
                {
                    string? output = await RunNetshAsync("winsock reset catalog", ct).ConfigureAwait(false);
                    return (true, (output ?? "").Contains("restart", StringComparison.OrdinalIgnoreCase)
                        ? "⚠️ נדרשת הפעלה מחדש" : "הושלם", true);
                });
        }

        if (resetTcpIp)
        {
            requiresReboot = true;
            string logPath = Path.Combine(Environment.SystemDirectory, "resetlog.txt");
            yield return await RunStepAsync("TCP/IP Reset", "איפוס TCP/IP stack...", ct,
                async () =>
                {
                    string? output = await RunNetshAsync($"int ip reset \"{logPath}\"", ct).ConfigureAwait(false);
                    return (true, "⚠️ נדרשת הפעלה מחדש", true);
                });
        }

        yield return new NetworkFlushStep(
            "סיכום",
            requiresReboot ? "⚠️ נדרשת הפעלה מחדש כדי שכל השינויים ייכנסו לתוקף"
                           : "✅ איפוס הרשת הושלם",
            true, false, requiresReboot);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task<NetworkFlushStep> RunStepAsync(
        string name, string message, CancellationToken ct,
        Func<Task<(bool ok, string detail, bool reboot)>> action)
    {
        log.LogInformation("Network flush step: {Name}", name);
        try
        {
            var (ok, detail, reboot) = await action().ConfigureAwait(false);
            return new NetworkFlushStep(name, detail, ok, false, reboot);
        }
        catch (Exception ex)
        {
            log.LogWarning("Step {Name} failed: {Msg}", name, ex.Message);
            return new NetworkFlushStep(name, ex.Message, false, false, false);
        }
    }

    private async Task<NetworkFlushStep> RunStepAsync(
        string name, string message, CancellationToken ct,
        Func<(bool ok, string detail, bool reboot)> action)
        => await RunStepAsync(name, message, ct, () => Task.FromResult(action())).ConfigureAwait(false);

    private static async Task<string?> RunNetshAsync(string args, CancellationToken ct)
    {
        using var proc = Process.Start(new ProcessStartInfo("netsh.exe", args)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        })!;
        string output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return output;
    }

    private static async Task<string?> RunIpconfigAsync(string verb, CancellationToken ct)
    {
        using var proc = Process.Start(new ProcessStartInfo("ipconfig.exe", $"/{verb}")
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            CreateNoWindow         = true
        })!;
        string output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return output;
    }
}
