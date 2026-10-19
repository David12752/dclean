namespace DClean.Engine.Safety;

public static class ProcessBlacklist
{
    public static readonly IReadOnlySet<string> CriticalProcessNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System",
            "Registry",
            "smss.exe",
            "csrss.exe",
            "wininit.exe",
            "winlogon.exe",
            "lsass.exe",
            "lsaiso.exe",
            "services.exe",
            "svchost.exe",
            "dwm.exe",
            "fontdrvhost.exe",

            // ── Shell & Desktop ──────────────────────────────────────────────
            "explorer.exe",          // Killing explorer is non-fatal but destructive to UX
            "sihost.exe",            // Shell Infrastructure Host
            "ShellExperienceHost.exe",
            "StartMenuExperienceHost.exe",
            "Idle",

            // ── Security & Antimalware ───────────────────────────────────────
            "MsMpEng.exe",
            "SecurityHealthService.exe",
            "SgrmBroker.exe",

            // ── DClean itself ────────────────────────────────────────────────
            "DClean.Service.exe",
            "DClean.UI.exe",
        };

    public static readonly IReadOnlySet<string> ProtectedProcessNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "audiodg.exe",
            "WerFault.exe",
            "spoolsv.exe",
        };

    public static bool IsCritical(string processName) =>
        CriticalProcessNames.Contains(processName) ||
        CriticalProcessNames.Contains(Path.GetFileName(processName));

    public static bool IsProtected(string processName) =>
        ProtectedProcessNames.Contains(processName) ||
        ProtectedProcessNames.Contains(Path.GetFileName(processName));
}
