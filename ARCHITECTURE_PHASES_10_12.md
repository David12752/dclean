# DClean — Phases 10–12: Audio Reset, Network Flush, WinSxS, SSD S.M.A.R.T.
> **Zero-Bricking Policy** — אין פעולה הרסנית ללא אישור + snapshot.  
> ממשק: WPF, עברית מלאה, FlowDirection=RightToLeft.

---

## PHASE 10 — Context Menu Manager + WinGet + DAW Cleaner
*(ראה ARCHITECTURE_PHASES_6_9.md — Modules F, G, H)*

---

## PHASE 11 — Audio Endpoint Reset & Network Pipeline Flush

### Module I: Audio Endpoint & Privacy Reset (WASAPI)

#### גילוי Sessions פעילות

```csharp
// NuGet: NAudio.Wasapi (wrapper ל-Windows Core Audio COM)
// או P/Invoke ישיר ל-mmdevapi.dll

using var enumerator = new MMDeviceEnumerator();
var devices = enumerator.EnumerateAudioEndPoints(
    DataFlow.Render, DeviceState.Active);

foreach (var device in devices) {
    var sessionMgr = AudioSessionManager2.FromMMDevice(device);
    var sessions   = sessionMgr.GetSessionEnumerator();

    foreach (var session in sessions) {
        using var ctrl = session.QueryInterface<AudioSessionControl2>();
        uint pid    = ctrl.GetProcessID();
        string name = Process.GetProcessById((int)pid).ProcessName;
        AudioSessionState state = ctrl.GetState();
        bool isExclusive = device.AudioClient.ShareMode
                           == AudioClientShareMode.Exclusive;
        yield return new AudioSession(name, pid, state, isExclusive, device.FriendlyName);
    }
}
```

#### זיהוי Exclusive Mode Lock

```csharp
// בדיקה: האם endpoint נעול ב-exclusive mode?
bool IsExclusiveLocked(MMDevice device) {
    try {
        var client = device.AudioClient;
        var format = device.AudioClient.MixFormat;
        // ניסיון initialize ב-shared mode — אם נכשל ב-AUDCLNT_E_DEVICE_IN_USE
        client.Initialize(AudioClientShareMode.Shared,
            AudioClientStreamFlags.None,
            latency: 0, period: 0, format, Guid.Empty);
        return false;
    }
    catch (COMException ex)
        when (ex.HResult == unchecked((int)0x88890004)) { // AUDCLNT_E_DEVICE_IN_USE
        return true;
    }
}
```

#### שחרור Exclusive Lock — 3 שלבים

```csharp
async Task ReleaseExclusiveLock(AudioSession lockingSession) {
    // שלב 1: WM_CLOSE — בקשה עדינה
    var hWnd = FindMainWindow(lockingSession.Pid);
    if (hWnd != IntPtr.Zero) {
        PostMessage(hWnd, WM_CLOSE, 0, 0);
        await Task.Delay(2000);
    }

    // שלב 2: עדיין נעול → הצג למשתמש
    // "Reaper נועל את ה-endpoint. לסגור את התוכנה?"
    if (!await WaitForRelease(timeoutMs: 3000)) {
        bool confirmed = await ConfirmDialog(
            $"התוכנה '{lockingSession.ProcessName}' מחזיקה בשמע באופן בלעדי.\nלסגור אותה?");
        if (confirmed)
            Process.GetProcessById((int)lockingSession.Pid)
                   .Kill(entireProcessTree: true);
    }

    // שלב 3: אפס שירות שמע (פותר הכל)
    await RestartAudioService();
}

async Task RestartAudioService() {
    // AudioSrv תלוי ב-AudioEndpointBuilder
    using var audioSrv = new ServiceController("AudioSrv");
    using var aebSrv   = new ServiceController("AudioEndpointBuilder");

    aebSrv.Stop(); await WaitForStatus(aebSrv, ServiceControllerStatus.Stopped);
    audioSrv.Stop(); await WaitForStatus(audioSrv, ServiceControllerStatus.Stopped);
    await Task.Delay(1500);
    audioSrv.Start(); await WaitForStatus(audioSrv, ServiceControllerStatus.Running);
    aebSrv.Start();
}
```

#### Privacy Locks — Microphone / Camera

```csharp
// קריאת סטטוס
RegistryKey GetConsentStore(string device) =>
    Registry.CurrentUser.OpenSubKey(
        $@"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{device}",
        writable: true)!;

// רשימת apps עם גישה
IEnumerable<AppPrivacy> EnumerateAppAccess(string device) {
    using var store = GetConsentStore(device);
    foreach (string appName in store.GetSubKeyNames()) {
        using var appKey = store.OpenSubKey(appName);
        string value = (string?)appKey?.GetValue("Value") ?? "Allow";
        yield return new AppPrivacy(appName, value == "Allow");
    }
}

// toggle גישה
void SetAppAccess(string device, string appName, bool allow) {
    using var key = GetConsentStore(device).OpenSubKey(appName, writable: true)
                    ?? GetConsentStore(device).CreateSubKey(appName);
    key.SetValue("Value", allow ? "Allow" : "Deny");
}

// כיבוי גלובלי (Policy)
void DisableGlobal(string device) =>
    Registry.LocalMachine
        .CreateSubKey($@"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy")
        .SetValue($"LetAppsAccess{char.ToUpper(device[0]) + device[1..]}", 2);
        // 2 = Force Deny
```

**UI — "מנהל שמע ופרטיות":**
```
┌──────────────────────────────────────────────────────┐
│  מצב Audio Endpoints                                 │
│  ──────────────────────────────────────────────────  │
│  🔊 Speakers (Realtek)    🟢 זמין — 3 sessions       │
│     Chrome · Discord · System Sounds                 │
│  🎧 ASIO (Focusrite)      🔴 נעול ע"י: Reaper        │
│     [🔓 שחרר נעילה]  [🔄 אפס שירות שמע]            │
│                                                      │
│  🎤 הרשאות מיקרופון                                 │
│     Zoom ✅  Chrome ✅  Discord ❌  OBS ✅           │
│                                                      │
│  📷 הרשאות מצלמה                                    │
│     Teams ✅  Zoom ✅  Chrome ❌                     │
└──────────────────────────────────────────────────────┘
```

---

### Module J: Network Pipeline Flush (SOS Network Reset)

**רצף פעולות — מסודר לפי השפעה:**

```csharp
// ── שלב 1: DNS Cache Flush (Native API — ללא CMD) ──
[DllImport("dnsapi.dll", EntryPoint="DnsFlushResolverCache")]
static extern bool DnsFlushResolverCache();

bool ok = DnsFlushResolverCache();
// מיידי, ללא תהליך חיצוני, ללא reboot

// ── שלב 2: Release/Renew DHCP (iphlpapi.dll) ──
[DllImport("iphlpapi.dll")]
static extern uint GetInterfaceInfo(IntPtr pIfTable, ref uint dwOutBufLen);
[DllImport("iphlpapi.dll")]
static extern uint IpReleaseAddress(ref IP_ADAPTER_INDEX_MAP AdapterInfo);
[DllImport("iphlpapi.dll")]
static extern uint IpRenewAddress(ref IP_ADAPTER_INDEX_MAP AdapterInfo);

foreach (var adapter in GetAdapters()) {
    IpReleaseAddress(ref adapter);
    await Task.Delay(500);
    IpRenewAddress(ref adapter);
}

// ── שלב 3: Flush ARP Cache ──
[DllImport("iphlpapi.dll")]
static extern uint GetIpNetTable(IntPtr pIpNetTable, ref uint pdwSize, bool bOrder);
[DllImport("iphlpapi.dll")]
static extern uint DeleteIpNetEntry(ref MIB_IPNETROW pArpEntry);

// Enumerate all ARP entries → DeleteIpNetEntry for each non-static entry

// ── שלב 4: NetBIOS Cache ──
// API ישיר אינו קיים — חייב CMD wrapper:
await RunAsSystem("nbtstat.exe", "-R");

// ── שלב 5: Winsock Catalog Reset ──
// אין managed API — netsh wrapper הוא הדרך הנכונה
// (WSCInstallProvider / WSCRemoveProvider אינם מספיקים לreset מלא)
await RunAsSystem("netsh.exe", "winsock reset catalog");
_requiresReboot = true;

// ── שלב 6: TCP/IP Stack Reset ──
await RunAsSystem("netsh.exe", "int ip reset resetlog.txt");
_requiresReboot = true;

// ── שלב 7: קריאת resetlog.txt — הצג ב-UI ──
string log = await File.ReadAllTextAsync(
    Path.Combine(Environment.SystemDirectory, "resetlog.txt"));
ParseResetLog(log); // → מוצג בתיבת פרטים
```

**Helper — RunAsSystem בתוך ה-service:**
```csharp
// ה-service רץ כ-SYSTEM — פשוט Process.Start
async Task RunAsSystem(string exe, string args) {
    using var proc = Process.Start(new ProcessStartInfo(exe, args) {
        CreateNoWindow     = true,
        UseShellExecute    = false,
        RedirectStandardOutput = true
    })!;
    string output = await proc.StandardOutput.ReadToEndAsync();
    await proc.WaitForExitAsync();
    AddLog(output);
}
```

**לוג בזמן אמת — Hebrew:**
```
🔄 מנקה DNS cache (Native API)...     ✅
📡 משחרר ומחדש IP address...          ✅  192.168.1.105
🗃️  מנקה ARP cache...                 ✅  12 entries cleared
💻 מנקה NetBIOS cache...              ✅
🔌 מאפס Winsock catalog...            ✅  ⚠️ נדרשת הפעלה מחדש
🔧 מאפס TCP/IP stack...               ✅  ⚠️ נדרשת הפעלה מחדש
─────────────────────────────────────────────────────
✅ איפוס רשת הושלם
⚠️  מומלץ: הפעל מחדש את המחשב כדי שהשינויים ייכנסו לתוקף
[🔄 הפעל מחדש עכשיו]  [⏰ הפעל מחדש בעוד שעה]  [❌ בטל]
```

---

## PHASE 12 — WinSxS Cleanup & SSD S.M.A.R.T. Analyzer

### Module K: Deep WinSxS & Component Store Cleanup

#### DISM Integration

```csharp
// שיטה א — DISM CLI (פשוט, stable, parse output)
// רץ בתוך ה-service (elevation מובנה)

async Task<DismResult> RunComponentCleanup(
    bool resetBase, IProgress<int> progress, CancellationToken ct) {

    // שלב 1: ניתוח מצב Component Store
    string analysisOutput = await RunCaptureAsync("dism.exe",
        "/Online /Cleanup-Image /AnalyzeComponentStore");
    var analysis = ParseDismAnalysis(analysisOutput);
    // → ComponentStorSize, ReclaimablePackages, RecommendedAction

    // שלב 2: ניקוי (עם או בלי /ResetBase)
    string cleanArgs = resetBase
        ? "/Online /Cleanup-Image /StartComponentCleanup /ResetBase"
        : "/Online /Cleanup-Image /StartComponentCleanup";

    // DISM כותב progress לקובץ log — poll הקובץ
    _ = Task.Run(() => PollDismProgress(progress, ct), ct);

    string result = await RunCaptureAsync("dism.exe", cleanArgs, ct);
    return ParseDismResult(result);
}

// Parse DISM AnalyzeComponentStore output
DismAnalysis ParseDismAnalysis(string output) {
    long size      = ParseSizeGB(output, "Component Store Size (Estimated)");
    long reclaimMb = ParseSizeGB(output, "Reclaimable Packages");
    string recommendation = ExtractLine(output, "Recommended Action");
    return new DismAnalysis(size, reclaimMb, recommendation);
}

// Poll progress מ-DISM log file
void PollDismProgress(IProgress<int> progress, CancellationToken ct) {
    string logPath = @"C:\Windows\Logs\DISM\dism.log";
    int last = 0;
    while (!ct.IsCancellationRequested) {
        string log = ReadTailSafe(logPath, 8192);
        var match  = Regex.Match(log, @"(\d+\.\d+)% complete");
        if (match.Success) {
            int pct = (int)double.Parse(match.Groups[1].Value);
            if (pct > last) { progress.Report(pct); last = pct; }
        }
        Thread.Sleep(500);
    }
}
```

#### SFC Scan + Log Parser

```csharp
async Task<SfcResult> RunSfcScan(CancellationToken ct) {
    // SFC חייב לרוץ כ-SYSTEM — ה-service מספק זאת
    await RunAsSystem("sfc.exe", "/scannow");

    // CBS.log — UTF-16 LE
    string logPath = Path.Combine(
        Environment.GetFolderPath(SpecialFolder.Windows),
        "Logs", "CBS", "CBS.log");
    string log = await File.ReadAllTextAsync(logPath, Encoding.Unicode, ct);

    // Parse תוצאות
    var corrupt  = Regex.Matches(log, @"\[SR\] Found corrupt file\s+(.+)$",
                       RegexOptions.Multiline);
    var repaired = Regex.Matches(log, @"\[SR\] Repaired file\s+(.+)$",
                       RegexOptions.Multiline);
    bool success = log.Contains("did not find any integrity violations")
                || log.Contains("successfully repaired");

    return new SfcResult(
        corrupt .Select(m => m.Groups[1].Value.Trim()).ToList(),
        repaired.Select(m => m.Groups[1].Value.Trim()).ToList(),
        success);
}
```

**UI — "ניקוי Component Store":**
```
┌──────────────────────────────────────────────────────┐
│  ניקוי מאגר רכיבי Windows (WinSxS)                  │
│  ──────────────────────────────────────────────────  │
│  גודל נוכחי:          12.4 GB                        │
│  ניתן לפינוי:          3.8 GB                        │
│  המלצה: ניקוי מומלץ                                  │
│                                                      │
│  ☑️  StartComponentCleanup (בטוח)                   │
│  ☐   ResetBase (לא ניתן לחזור — הסר SP ישנים)       │
│                                                      │
│  [🔍 בדוק שלמות קבצים (SFC)]  [🧹 נקה עכשיו]       │
│  ──────────────────────────────────────────────────  │
│  ⏳ 34% — מעבד חבילות...                            │
└──────────────────────────────────────────────────────┘
```

---

### Module L: SSD Health Monitor & S.M.A.R.T. Analyzer

#### שאילתת S.M.A.R.T. — DeviceIoControl

```csharp
// פתח Physical Drive (דורש elevation)
SafeFileHandle hDrive = CreateFile(@"\\.\PhysicalDrive0",
    GENERIC_READ | GENERIC_WRITE,
    FILE_SHARE_READ | FILE_SHARE_WRITE,
    null, OPEN_EXISTING, 0, IntPtr.Zero);

// שלח SMART READ ATTRIBUTES
var cmdIn = new SENDCMDINPARAMS {
    bDriveNumber = 0,
    irDriveRegs  = new IDEREGS {
        bFeaturesReg = READ_ATTRIBUTES,  // 0xD0
        bSectorCountReg = 1,
        bSectorNumberReg = 1,
        bCylLowReg  = 0x4F,
        bCylHighReg = 0xC2,
        bDriveHeadReg = 0xA0,
        bCommandReg = SMART_CMD  // 0xB0
    }
};

DeviceIoControl(hDrive, SMART_RCV_DRIVE_DATA,
    ref cmdIn, Marshal.SizeOf<SENDCMDINPARAMS>() - 1,
    out SENDCMDOUTPARAMS cmdOut, Marshal.SizeOf<SENDCMDOUTPARAMS>(),
    out uint bytesRet, IntPtr.Zero);

// Parse 30 attributes מ-cmdOut.bBuffer
var attributes = ParseSmartAttributes(cmdOut.bBuffer);
```

#### S.M.A.R.T. Attribute Interpretation

```csharp
// Critical attributes — HDD + SSD
static readonly Dictionary<byte, SmartAttr> KnownAttrs = new() {
    [0x01] = new("Raw Read Error Rate",     Critical: false),
    [0x05] = new("Reallocated Sectors Count", Critical: true),  // > 0 = danger
    [0x09] = new("Power-On Hours",          Critical: false),
    [0x0C] = new("Power Cycle Count",       Critical: false),
    [0xAB] = new("Program Fail Count (SSD)", Critical: true),
    [0xAC] = new("Erase Fail Count (SSD)",  Critical: true),
    [0xAD] = new("Wear Leveling Count",     Critical: false),  // SSD lifespan
    [0xAE] = new("Unexpected Power Loss",   Critical: false),
    [0xB1] = new("Wear Range Delta",        Critical: false),
    [0xB8] = new("End-to-End Error",        Critical: true),
    [0xBB] = new("Uncorrectable Error Count", Critical: true),
    [0xBC] = new("Command Timeout",         Critical: false),
    [0xBE] = new("Airflow Temperature",     Critical: false),
    [0xC0] = new("Unsafe Shutdown Count",   Critical: false),
    [0xC2] = new("Temperature",             Critical: false),
    [0xC5] = new("Current Pending Sectors", Critical: true),   // imminent failure
    [0xC6] = new("Offline Uncorrectable",   Critical: true),   // imminent failure
    [0xC7] = new("Ultra DMA CRC Error",     Critical: false),
    [0xE1] = new("Load/Unload Cycle Count", Critical: false),
    [0xE9] = new("NAND Writes (1GB)",       Critical: false),  // TBW tracking
    [0xF1] = new("Total Host Writes",       Critical: false),  // TBW primary
    [0xF2] = new("Total Host Reads",        Critical: false),
};

// Health Score חישוב
int CalcHealthScore(List<SmartAttribute> attrs) {
    int score = 100;
    foreach (var a in attrs) {
        if (!KnownAttrs.TryGetValue(a.Id, out var def)) continue;
        if (!def.Critical) continue;

        // Normalized value < threshold = pre-failure warning
        if (a.Value < a.Threshold) score -= 30;
        // Raw value > 0 for critical attrs = deductions
        else if (a.RawValue > 0)   score -= 10;
    }
    return Math.Max(0, score);
}
```

#### TBW ו-SSD Lifespan

```csharp
// Total Bytes Written — מ-S.M.A.R.T. attr F1 (Total Host Writes)
// Raw value בדרך כלל ב-GB או LBA units — תלוי יצרן
long GetTotalBytesWritten(List<SmartAttribute> attrs) {
    // Attr 0xF1 — Samsung, WD, Seagate
    var f1 = attrs.FirstOrDefault(a => a.Id == 0xF1);
    if (f1 != null) return f1.RawValue * 1_073_741_824L; // GB → bytes

    // Attr 0xE9 — Intel SSDs (units: 1GB)
    var e9 = attrs.FirstOrDefault(a => a.Id == 0xE9);
    if (e9 != null) return e9.RawValue * 1_073_741_824L;

    return -1; // לא זמין
}

// TBW מהמפרט — לוד מ-DB/config לפי דגם
// lifespan % = TBW_used / TBW_rated * 100
double GetLifespanPercent(string modelName, long bytesWritten) {
    long ratedTbw = _ssdDatabase.GetRatedTbw(modelName) ?? 300_000_000_000L; // 300TB default
    return (double)bytesWritten / ratedTbw * 100.0;
}
```

#### TRIM Status Check

```csharp
// בדיקת TRIM — fsutil
string output = await RunCaptureAsync("fsutil.exe", "behavior query DisableDeleteNotify");
// DisableDeleteNotify = 0 → TRIM מופעל ✅
// DisableDeleteNotify = 1 → TRIM כבוי ⚠️

// הפעלת TRIM (אם כבוי):
await RunAsSystem("fsutil.exe", "behavior set DisableDeleteNotify 0");
```

#### WMI Fallback (פחות גישה ישירה, אבל ללא elevation בחלק מהמקרים)

```csharp
// MSStorageDriver_ATAPISmartData — דורש elevation
using var searcher = new ManagementObjectSearcher(
    @"root\WMI",
    "SELECT * FROM MSStorageDriver_ATAPISmartData");

foreach (ManagementObject obj in searcher.Get()) {
    byte[] data = (byte[])obj["VendorSpecific"];
    // data[2..] = 30 × 12-byte attribute structs
    // struct: [AttrId, Flags, Value, WorstValue, RawValue(6 bytes), Reserved]
    var attrs = ParseSmartBuffer(data);
}
```

**UI — "בריאות כוננים":**
```
┌──────────────────────────────────────────────────────┐
│  בריאות כוננים                                       │
│  ──────────────────────────────────────────────────  │
│  💾 Samsung 970 EVO Plus 1TB (C:\)                   │
│     ציון בריאות:  ██████████  92%  🟢 טוב            │
│     טמפרטורה:     38°C                               │
│     שעות הפעלה:  14,320h (≈ 1.6 שנים)               │
│     נתונים שנכתבו: 18.4 TB / 600 TB rated (3%)      │
│     TRIM:        ✅ פעיל                             │
│                                                      │
│  S.M.A.R.T. Attributes           ↕ הצג הכל          │
│  ────────────────────────────────────────────        │
│  Reallocated Sectors   0     ✅  תקין                │
│  Current Pending       0     ✅  תקין                │
│  Uncorrectable Errors  0     ✅  תקין                │
│  Wear Leveling Count  94     ✅  תקין                │
│  ⚠️  Program Fail Count  2    ⚠️  שים לב             │
│                                                      │
│  [📊 ייצא דוח]  [🔄 רענן]                           │
└──────────────────────────────────────────────────────┘
```

---

## סיכום — מפת Modules

| Phase | Module | תיאור | APIs עיקריים |
|---|---|---|---|
| 6 | Deep Uninstaller | MSI + heuristic leftover scan | `msi.dll`, Registry, VSS |
| 7A | Driver Auditor | Audit + manual update | `setupapi.dll`, DISM, WU API |
| 7B | Startup Optimizer | Run/Task/Service manager | `advapi32.dll`, Task Scheduler |
| 8C | Perf Tuner | Ultimate power plan, core parking | `powrprof.dll` |
| 8D | MFT Scanner | Large file + duplicate finder | `DeviceIoControl` FSCTL_ENUM_USN_DATA |
| 8E | Debloater | Appx removal + telemetry | `PackageManager`, Registry |
| 9F | Context Menu Mgr | Shell extension on/off | Registry HKCR |
| 9G | WinGet Updater | Silent software updates | `winget.exe` IPC |
| 9H | DAW Cleaner | VST orphans + cache + broken libs | Registry, SQLite, PE |
| 11I | Audio Reset | WASAPI exclusive lock release | `mmdevapi.dll` COM |
| 11J | Network Flush | DNS + Winsock + TCP/IP reset | `dnsapi.dll`, `iphlpapi.dll` |
| 12K | WinSxS Cleanup | Component store + SFC | `dism.exe` CLI, `sfc.exe` |
| 12L | SSD S.M.A.R.T. | Drive health + TBW + TRIM | `DeviceIoControl` SMART |

---

## הנחיות אדריכלות מרכזיות

### חלוקת אחריות

```
DClean.Engine.*     ← כל logic OS-level (P/Invoke, WMI, COM)
DClean.Service      ← elevation, pipe server, ריצת commands
DClean.UI           ← WPF views, ViewModels, Hebrew RTL
DClean.Contracts    ← shared DTOs בין service ל-UI
```

### Pattern לכל Module חדש

```csharp
// 1. Engine class (DClean.Engine.{Module}\{Name}Engine.cs)
public sealed class UninstallerEngine(
    PathValidator validator,
    DryRunGuard guard,
    ILogger<UninstallerEngine> log) { ... }

// 2. DTO ב-Contracts
record InstalledApp(...);
record LeftoverCandidate(...);

// 3. Pipe messages חדשים (PipeMessageType enum)
UninstallRequest, LeftoverScanRequest, LeftoverScanProgress, UninstallComplete

// 4. ViewModel (CommunityToolkit.Mvvm)
public sealed partial class UninstallerViewModel : ObservableObject {
    [ObservableProperty] ObservableCollection<InstalledAppViewModel> _apps = [];
    [RelayCommand] async Task ScanLeftoversAsync(InstalledApp app) { ... }
}

// 5. View (WPF, FlowDirection=RightToLeft, CardStyle, NavButtonStyle)
```

### 32-bit vs 64-bit — Registry Redirection

```csharp
// תמיד פתח שניהם במפורש:
using var key64 = RegistryKey.OpenBaseKey(
    RegistryHive.LocalMachine, RegistryView.Registry64)
    .OpenSubKey(path);
using var key32 = RegistryKey.OpenBaseKey(
    RegistryHive.LocalMachine, RegistryView.Registry32)
    .OpenSubKey(path);
// WOW64 Reflection כבוי ב-Windows Vista+ — חייבים שתי גישות נפרדות
```
