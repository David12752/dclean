using DClean.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace DClean.Engine.Audio;

[SupportedOSPlatform("windows")]
public sealed class AudioEndpointEngine(ILogger<AudioEndpointEngine> log)
{
    // ── Endpoint enumeration (via registry + running processes) ─────────

    public IReadOnlyList<AudioEndpointInfo> EnumerateEndpoints()
    {
        var results = new List<AudioEndpointInfo>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render");
            if (key == null) return results;

            foreach (string id in key.GetSubKeyNames())
            {
                using var dev = key.OpenSubKey(id);
                if (dev == null) continue;

                int state = (int)(dev.GetValue("DeviceState") ?? 0);
                if (state != 1) continue; // 1 = DEVICE_STATE_ACTIVE

                using var props = dev.OpenSubKey("Properties");
                string name = props?.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string
                           ?? id;
                string desc = props?.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},0") as string
                           ?? "";

                results.Add(new AudioEndpointInfo(id, name, desc, IsEndpointLocked(name)));
            }
        }
        catch (Exception ex)
        {
            log.LogWarning("Endpoint enumeration failed: {Msg}", ex.Message);
        }
        return results;
    }

    // ── Privacy (microphone / camera) ─────────────────────────────────

    public IReadOnlyList<PrivacyAppEntry> GetPrivacyEntries(string device)
    {
        var results = new List<PrivacyAppEntry>();
        try
        {
            using var store = Registry.CurrentUser.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{device}");
            if (store == null) return results;

            foreach (string appName in store.GetSubKeyNames())
            {
                if (appName == "NonPackaged") continue;
                using var appKey = store.OpenSubKey(appName);
                string val = appKey?.GetValue("Value") as string ?? "Allow";
                results.Add(new PrivacyAppEntry(appName, val == "Allow", device));
            }

            // NonPackaged apps
            using var np = store.OpenSubKey("NonPackaged");
            if (np != null)
                foreach (string app in np.GetSubKeyNames())
                {
                    using var appKey = np.OpenSubKey(app);
                    string val = appKey?.GetValue("Value") as string ?? "Allow";
                    results.Add(new PrivacyAppEntry(
                        app.Replace("#", @"\"), val == "Allow", device));
                }
        }
        catch (Exception ex)
        {
            log.LogWarning("Privacy entries {Device} failed: {Msg}", device, ex.Message);
        }
        return results;
    }

    public bool SetPrivacyAccess(string device, string appName, bool allow)
    {
        try
        {
            string subPath = appName.Contains('\\')
                ? $@"NonPackaged\{appName.Replace(@"\", "#")}"
                : appName;

            using var key = Registry.CurrentUser.CreateSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{device}\{subPath}",
                writable: true);
            key.SetValue("Value", allow ? "Allow" : "Deny");
            return true;
        }
        catch (Exception ex)
        {
            log.LogError("SetPrivacyAccess failed: {Msg}", ex.Message);
            return false;
        }
    }

    // ── Audio service restart ─────────────────────────────────────────

    public async Task<bool> RestartAudioServiceAsync(CancellationToken ct = default)
    {
        log.LogInformation("Restarting audio services...");
        try
        {
            using var aeb  = new ServiceController("AudioEndpointBuilder");
            using var audio= new ServiceController("AudioSrv");

            if (audio.Status == ServiceControllerStatus.Running)
            {
                audio.Stop();
                await WaitStatusAsync(audio, ServiceControllerStatus.Stopped, ct).ConfigureAwait(false);
            }
            if (aeb.Status == ServiceControllerStatus.Running)
            {
                aeb.Stop();
                await WaitStatusAsync(aeb, ServiceControllerStatus.Stopped, ct).ConfigureAwait(false);
            }

            await Task.Delay(1500, ct).ConfigureAwait(false);

            audio.Start();
            await WaitStatusAsync(audio, ServiceControllerStatus.Running, ct).ConfigureAwait(false);
            aeb.Start();

            log.LogInformation("Audio services restarted successfully.");
            return true;
        }
        catch (Exception ex)
        {
            log.LogError("Audio service restart failed: {Msg}", ex.Message);
            return false;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static bool IsEndpointLocked(string friendlyName)
    {
        // Check if any exclusive-mode process exists (heuristic)
        try
        {
            var exclusiveApps = new[] { "reaper", "cubase", "live", "fl64", "bitwig",
                "protools", "studio one", "nuendo", "vegas" };
            return Process.GetProcesses().Any(p =>
            {
                try
                {
                    return exclusiveApps.Any(a =>
                        p.ProcessName.Contains(a, StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception) { return false; }
            });
        }
        catch (Exception) { return false; }
    }

    private static async Task WaitStatusAsync(
        ServiceController svc, ServiceControllerStatus target, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(15);
        await Task.Run(() => svc.WaitForStatus(target, timeout), ct).ConfigureAwait(false);
    }
}
