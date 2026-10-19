using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DClean.Contracts;
using DClean.UI.Services;
using System.Collections.ObjectModel;
using System.IO;

namespace DClean.UI.ViewModels;

public enum FindingSeverity { Critical, Warning, Info }

public sealed record ScanFinding(
    FindingSeverity Severity,
    string          Category,
    string          Title,
    string          Detail,
    string          NavTarget,
    string          ActionLabel,
    string          Source = "",
    string          RelatedApplication = "",
    string          Location = "",
    string          ProblematicItem = "");

public sealed partial class FullScanViewModel : ObservableObject
{
    // ── Thresholds ─────────────────────────────────────────────────────────
    private const long   JunkWarningThresholdBytes   = 500_000_000;  // 500 MB
    private const long   JunkInfoThresholdBytes      = 10_000_000;   // 10 MB
    private const int    StartupItemWarningThreshold = 12;
    private const int    DriverAgeWarningYears       = 4;
    private const int    HealthScoreCritical         = 50;
    private const int    HealthScoreWarning          = 75;
    private const long   DawCacheWarningBytes        = 100_000_000;  // 100 MB

    private readonly IPipeClient _client;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool   _isScanning   = false;
    [ObservableProperty] private string _statusText   = "לחץ 'סרוק הכל' לבדיקה מקיפה של המערכת";
    [ObservableProperty] private int    _progress     = 0;
    [ObservableProperty] private int    _criticalCount = 0;
    [ObservableProperty] private int    _warningCount  = 0;
    [ObservableProperty] private int    _infoCount     = 0;
    [ObservableProperty] private bool   _hasScanned    = false;

    public ObservableCollection<ScanFinding> Findings { get; } = [];

    public IReadOnlyList<ScanFinding> CriticalFindings =>
        Findings.Where(f => f.Severity == FindingSeverity.Critical).ToList();
    public IReadOnlyList<ScanFinding> WarningFindings =>
        Findings.Where(f => f.Severity == FindingSeverity.Warning).ToList();
    public IReadOnlyList<ScanFinding> InfoFindings =>
        Findings.Where(f => f.Severity == FindingSeverity.Info).ToList();

    public Action<string>? NavigateToTarget { get; set; }

    public FullScanViewModel(IPipeClient client)
    {
        _client = client;
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAllAsync()
    {
        if (!await _client.EnsureConnectedAsync())
        {
            StatusText = "⚠️ לא מחובר לשירות — ודא שהשירות רץ ונסה שוב.";
            return;
        }

        IsScanning    = true;
        Progress      = 0;
        StatusText    = "מתחיל סריקה מקיפה...";
        _cts          = new CancellationTokenSource();
        Findings.Clear();
        CriticalCount = WarningCount = InfoCount = 0;

        // The pipe is serial — run scans one at a time.
        // Each has an individual timeout so a stuck module doesn't block everything.
        var modules = new (string Name, Func<Task<IEnumerable<ScanFinding>>> Scan)[]
        {
            ("קבצי זבל",      ScanCacheAsync),
            ("תוכניות אתחול", ScanStartupAsync),
            ("דרייברים",       ScanDriversAsync),
            ("S.M.A.R.T.",     ScanSmartAsync),
            ("DAW / פלאגינים", ScanDawAsync),
            ("שמע / פרטיות",   ScanAudioAsync),
            // WinGet last — it can be slow (winget CLI)
            ("עדכוני תוכנה",   ScanWinGetAsync),
        };

        try
        {
            for (int i = 0; i < modules.Length; i++)
            {
                if (_cts.Token.IsCancellationRequested) break;

                var (name, scan) = modules[i];
                StatusText = $"בודק {name}... ({i + 1}/{modules.Length})";

                try
                {
                    var findings = await scan();
                    foreach (var f in findings) AddFinding(f);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    AddFinding(new ScanFinding(FindingSeverity.Info,
                        name, $"לא ניתן לבדוק {name}", ex.Message, "", ""));
                }

                Progress = (i + 1) * 100 / modules.Length;
            }

            Progress   = 100;
            HasScanned = true;
            StatusText = CriticalCount > 0
                ? $"⚠️ נמצאו {CriticalCount} בעיות קריטיות, {WarningCount} אזהרות"
                : WarningCount > 0
                    ? $"✅ נמצאו {WarningCount} אזהרות — אין בעיות קריטיות"
                    : $"✅ המערכת תקינה — {InfoCount} פריטי מידע";

            OnPropertyChanged(nameof(CriticalFindings));
            OnPropertyChanged(nameof(WarningFindings));
            OnPropertyChanged(nameof(InfoFindings));
        }
        catch (OperationCanceledException) { StatusText = "בוטל על ידי המשתמש"; }
        catch (Exception ex)              { StatusText = $"שגיאה: {ex.Message}"; }
        finally
        {
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanScan() => !IsScanning;

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void Navigate(ScanFinding? finding)
    {
        if (finding == null) return;
        NavigateToTarget?.Invoke(finding.NavTarget);
    }

    // ── Individual scan methods ────────────────────────────────────────────

    private async Task<IEnumerable<ScanFinding>> ScanCacheAsync()
    {
        var findings = new List<ScanFinding>();
        try
        {
            int count = 0;
            long bytes = 0;
            string largestPath = string.Empty;
            long largestBytes = 0;
            await foreach (var r in _client.ScanAsync(dryRun: true, ct: _cts!.Token))
            {
                if (!r.Blocked)
                {
                    count++;
                    bytes += r.SizeBytes;
                    if (r.SizeBytes > largestBytes)
                    {
                        largestBytes = r.SizeBytes;
                        largestPath = r.Path;
                    }
                }
            }
            if (bytes > JunkWarningThresholdBytes)
                findings.Add(new ScanFinding(FindingSeverity.Warning, "קבצי זבל",
                    $"נמצאו {FormatHelper.FormatBytes(bytes)} קבצי זבל",
                    $"{count} קבצים מיותרים ניתנים לניקוי",
                    "dashboard", "נקה עכשיו",
                    Source: "Temporary/cache scan",
                    RelatedApplication: "מערכת וקבצי מטמון",
                    Location: string.IsNullOrWhiteSpace(largestPath) ? string.Empty : Path.GetDirectoryName(largestPath) ?? largestPath,
                    ProblematicItem: largestPath));
            else if (bytes > JunkInfoThresholdBytes)
                findings.Add(new ScanFinding(FindingSeverity.Info, "קבצי זבל",
                    $"נמצאו {FormatHelper.FormatBytes(bytes)} קבצי זבל",
                    $"{count} קבצים", "dashboard", "נקה",
                    Source: "Temporary/cache scan",
                    RelatedApplication: "מערכת וקבצי מטמון",
                    Location: string.IsNullOrWhiteSpace(largestPath) ? string.Empty : Path.GetDirectoryName(largestPath) ?? largestPath,
                    ProblematicItem: largestPath));
            else if (bytes == 0)
                findings.Add(new ScanFinding(FindingSeverity.Info, "קבצי זבל",
                    "לא נמצאו קבצי זבל", "המערכת נקייה", "dashboard", "עיין"));
        }
        catch (Exception ex) { findings.Add(new ScanFinding(FindingSeverity.Info, "קבצי זבל", $"שגיאת סריקה: {ex.Message}", string.Empty, "dashboard", "עיין")); }
        return findings;
    }

    private async Task<IEnumerable<ScanFinding>> ScanStartupAsync()
    {
        var findings = new List<ScanFinding>();
        try
        {
            var items = await _client.GetStartupItemsAsync(_cts!.Token);
            var enabled = items.Where(i => i.Status == StartupItemStatus.Enabled).ToList();
            if (enabled.Count > StartupItemWarningThreshold)
            {
                var sample = enabled[0];
                findings.Add(new ScanFinding(FindingSeverity.Warning, "תוכניות אתחול",
                    $"{enabled.Count} תוכניות מאטות את ההפעלה",
                    string.Join(", ", enabled.Take(5).Select(i => i.Name)) +
                    (enabled.Count > 5 ? $" ועוד {enabled.Count - 5}..." : ""),
                    "startup", "נהל אתחול",
                    Source: sample.Source.ToString(),
                    RelatedApplication: sample.Name,
                    Location: sample.RegistryPath ?? sample.Command,
                    ProblematicItem: sample.Command));
            }
            else
            {
                var sample = enabled.FirstOrDefault();
                findings.Add(new ScanFinding(FindingSeverity.Info, "תוכניות אתחול",
                    $"{enabled.Count} תוכניות בהפעלה", "האתחול נראה סביר",
                    "startup", "עיין",
                    Source: sample?.Source.ToString() ?? "Startup scan",
                    RelatedApplication: sample?.Name ?? string.Empty,
                    Location: sample?.RegistryPath ?? sample?.Command ?? string.Empty,
                    ProblematicItem: sample?.Command ?? string.Empty));
            }
        }
        catch (Exception ex) { findings.Add(new ScanFinding(FindingSeverity.Info, "תוכניות אתחול", $"שגיאת סריקה: {ex.Message}", string.Empty, "startup", "עיין")); }
        return findings;
    }

    private async Task<IEnumerable<ScanFinding>> ScanWinGetAsync()
    {
        var findings = new List<ScanFinding>();
        try
        {
            var outdated = await _client.GetOutdatedPackagesAsync(_cts!.Token);
            if (outdated.Count > 0)
            {
                var sample = outdated[0];
                bool hasCritical = outdated.Any(p =>
                    p.Name.Contains("security", StringComparison.OrdinalIgnoreCase) ||
                    p.Name.Contains("defender", StringComparison.OrdinalIgnoreCase) ||
                    p.Name.Contains("runtime", StringComparison.OrdinalIgnoreCase));
                findings.Add(new ScanFinding(
                    hasCritical ? FindingSeverity.Critical : FindingSeverity.Warning,
                    "עדכוני תוכנה",
                    $"{outdated.Count} תוכנות ממתינות לעדכון",
                    string.Join(", ", outdated.Take(4).Select(p => p.Name)) +
                    (outdated.Count > 4 ? $" ועוד {outdated.Count - 4}..." : ""),
                        "winget", "עדכן הכל",
                        Source: "WinGet package source",
                        RelatedApplication: sample.Name,
                        Location: sample.Source,
                        ProblematicItem: sample.Id));
            }
            else
                findings.Add(new ScanFinding(FindingSeverity.Info, "עדכוני תוכנה",
                    "כל התוכנות מעודכנות", "", "winget", "עיין"));
        }
        catch (Exception ex) { findings.Add(new ScanFinding(FindingSeverity.Info, "עדכוני תוכנה", $"שגיאת סריקה: {ex.Message}", string.Empty, "winget", "עיין")); }
        return findings;
    }

    private async Task<IEnumerable<ScanFinding>> ScanDriversAsync()
    {
        var findings = new List<ScanFinding>();
        try
        {
            var drivers = await _client.GetDriversAsync(_cts!.Token);
            var unsigned = drivers.Where(d => !d.IsSigned).ToList();
            var old = drivers.Where(d => d.DriverDate.HasValue &&
                d.DriverDate.Value < DateTime.Now.AddYears(-DriverAgeWarningYears)).ToList();

            if (unsigned.Count > 0)
            {
                foreach (var driver in unsigned.Take(5))
                {
                    findings.Add(new ScanFinding(FindingSeverity.Warning, "דרייברים",
                        $"הדרייבר {driver.DeviceName} אינו חתום",
                        $"ספק: {driver.Provider} · גרסה: {driver.Version}",
                        "drivers", "בדוק דרייברים",
                        Source: driver.DeviceClass,
                        RelatedApplication: driver.DeviceName,
                        Location: driver.Provider,
                        ProblematicItem: driver.InfName));
                }
            }

            if (old.Count > 5)
            {
                var sample = old[0];
                findings.Add(new ScanFinding(FindingSeverity.Info, "דרייברים",
                    $"{old.Count} דרייברים ישנים (מעל 4 שנים)",
                    "מומלץ לעדכן דרייברים ישנים", "drivers", "בדוק",
                    Source: sample.DeviceClass,
                    RelatedApplication: sample.DeviceName,
                    Location: sample.Provider,
                    ProblematicItem: sample.InfName));
            }
            else if (unsigned.Count == 0)
                findings.Add(new ScanFinding(FindingSeverity.Info, "דרייברים",
                    $"כל {drivers.Count} הדרייברים חתומים", "מצב תקין",
                    "drivers", "עיין"));
        }
        catch (Exception ex) { findings.Add(new ScanFinding(FindingSeverity.Info, "דרייברים", $"שגיאת סריקה: {ex.Message}", string.Empty, "drivers", "עיין")); }
        return findings;
    }

    private async Task<IEnumerable<ScanFinding>> ScanSmartAsync()
    {
        var findings = new List<ScanFinding>();
        try
        {
            var drives = await _client.GetDriveHealthAsync(_cts!.Token);
            foreach (var drive in drives)
            {
                if (drive.HealthScore < HealthScoreCritical)
                    findings.Add(new ScanFinding(FindingSeverity.Critical, "כוננים",
                        $"כונן {drive.Model} — מצב קריטי ({drive.HealthScore}/100)",
                        $"תמיד כבר עת לגבות את הנתונים!", "smart", "בדוק S.M.A.R.T.",
                        Source: "S.M.A.R.T.",
                        RelatedApplication: drive.Model,
                        Location: drive.SerialNumber,
                        ProblematicItem: drive.Attributes.FirstOrDefault(a => a.IsCritical && !a.IsHealthy)?.Name ?? string.Empty));
                else if (drive.HealthScore < HealthScoreWarning)
                    findings.Add(new ScanFinding(FindingSeverity.Warning, "כוננים",
                        $"כונן {drive.Model} — מצב לא מיטבי ({drive.HealthScore}/100)",
                        "מומלץ לגבות ולבדוק", "smart", "בדוק S.M.A.R.T.",
                        Source: "S.M.A.R.T.",
                        RelatedApplication: drive.Model,
                        Location: drive.SerialNumber,
                        ProblematicItem: drive.Attributes.FirstOrDefault(a => !a.IsHealthy)?.Name ?? string.Empty));
                else
                    findings.Add(new ScanFinding(FindingSeverity.Info, "כוננים",
                        $"כונן {drive.Model} — מצב טוב ({drive.HealthScore}/100)",
                        !drive.TrimEnabled ? "⚠️ TRIM כבוי — מומלץ להפעיל" : "תקין",
                        "smart", "עיין",
                        Source: "S.M.A.R.T.",
                        RelatedApplication: drive.Model,
                        Location: drive.SerialNumber,
                        ProblematicItem: drive.TrimEnabled ? string.Empty : "TRIM disabled"));
            }
        }
        catch (Exception ex) { findings.Add(new ScanFinding(FindingSeverity.Info, "כוננים", $"שגיאת סריקה: {ex.Message}", string.Empty, "smart", "עיין")); }
        return findings;
    }

    private async Task<IEnumerable<ScanFinding>> ScanDawAsync()
    {
        var findings = new List<ScanFinding>();
        try
        {
            var result = await _client.ScanDawAsync(_cts!.Token);
            if (result != null)
            {
                long total = result.Caches.Sum(c => c.SizeBytes);
                if (total > DawCacheWarningBytes)
                {
                    var sampleCache = result.Caches.OrderByDescending(c => c.SizeBytes).FirstOrDefault();
                    findings.Add(new ScanFinding(FindingSeverity.Info, "DAW",
                        $"{FormatHelper.FormatBytes(total)} cache של DAW",
                        $"{result.Caches.Count} תיקיות, {result.BrokenLibraries.Count} ספריות שבורות",
                        "daw", "נקה DAW",
                        Source: "DAW cache scan",
                        RelatedApplication: sampleCache?.Daw ?? string.Empty,
                        Location: sampleCache?.CachePath ?? string.Empty,
                        ProblematicItem: sampleCache?.CachePath ?? string.Empty));
                }
                if (result.BrokenLibraries.Count > 0)
                {
                    var sampleLibrary = result.BrokenLibraries[0];
                    findings.Add(new ScanFinding(FindingSeverity.Warning, "DAW",
                        $"{result.BrokenLibraries.Count} ספריות סמפלים שבורות",
                        string.Join(", ", result.BrokenLibraries.Take(3)
                            .Select(b => b.LibraryName)), "daw", "בדוק",
                        Source: sampleLibrary.Publisher,
                        RelatedApplication: sampleLibrary.LibraryName,
                        Location: sampleLibrary.BrokenPath,
                        ProblematicItem: sampleLibrary.BrokenPath));
                }
            }
        }
        catch (Exception ex) { findings.Add(new ScanFinding(FindingSeverity.Info, "DAW", $"שגיאת סריקה: {ex.Message}", string.Empty, "daw", "עיין")); }
        return findings;
    }

    private async Task<IEnumerable<ScanFinding>> ScanAudioAsync()
    {
        var findings = new List<ScanFinding>();
        try
        {
            var endpoints = await _client.GetAudioEndpointsAsync(_cts!.Token);
            var locked = endpoints.Where(e => e.IsExclusiveLocked).ToList();
            if (locked.Count > 0)
            {
                var sample = locked[0];
                findings.Add(new ScanFinding(FindingSeverity.Info, "שמע",
                    $"{locked.Count} נקודות קצה נעולות בבלעדיות",
                    string.Join(", ", locked.Select(e => e.FriendlyName)),
                    "audio", "נהל שמע",
                    Source: "Audio endpoint scan",
                    RelatedApplication: sample.FriendlyName,
                    Location: sample.Description,
                    ProblematicItem: sample.Id));
            }

            var micApps = await _client.GetPrivacyEntriesAsync("microphone", _cts.Token);
            var blocked = micApps.Where(a => !a.IsAllowed).ToList();
            if (blocked.Count > 0)
            {
                var sample = blocked[0];
                findings.Add(new ScanFinding(FindingSeverity.Info, "פרטיות",
                    $"{blocked.Count} אפליקציות חסומות מהמיקרופון",
                    string.Join(", ", blocked.Take(3).Select(a => a.AppName)),
                    "audio", "נהל פרטיות",
                    Source: "Privacy permissions",
                    RelatedApplication: sample.AppName,
                    Location: "microphone",
                    ProblematicItem: sample.AppName));
            }
        }
        catch (Exception ex) { findings.Add(new ScanFinding(FindingSeverity.Info, "שמע", $"שגיאת סריקה: {ex.Message}", string.Empty, "audio", "עיין")); }
        return findings;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void AddFinding(ScanFinding f)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            Findings.Add(f);
            switch (f.Severity)
            {
                case FindingSeverity.Critical: CriticalCount++; break;
                case FindingSeverity.Warning:  WarningCount++;  break;
                case FindingSeverity.Info:     InfoCount++;     break;
            }
            OnPropertyChanged(nameof(CriticalFindings));
            OnPropertyChanged(nameof(WarningFindings));
            OnPropertyChanged(nameof(InfoFindings));
        });
    }
}
