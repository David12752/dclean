# DClean — Phases 6–9: Deep Uninstaller, Driver Auditor, Performance Tuner, DAW Cleaner
> **Zero-Bricking Policy** — כל פעולה הרסנית מוגנת ב-snapshot לפני ביצוע.  
> ממשק: WPF, עברית מלאה, FlowDirection=RightToLeft.

---

## PHASE 6 — Deep Uninstaller (מסיר התקנות מעמיק)

### 6.1 גילוי יישומים מותקנים

**API ראשי — MSI (msi.dll, P/Invoke):**
```csharp
[DllImport("msi.dll", CharSet=CharSet.Unicode)]
static extern uint MsiEnumProducts(uint index, StringBuilder productCode);

[DllImport("msi.dll", CharSet=CharSet.Unicode)]
static extern uint MsiGetProductInfo(string product, string property,
    StringBuilder value, ref uint size);

// Properties: INSTALLPROPERTY_PRODUCTNAME, _PUBLISHER, _VERSIONSTRING,
//             _INSTALLLOCATION, _UNINSTALLSTRING, _ESTIMATEDSIZE
```

**Fallback — Registry scan (כולל WOW64):**
```
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*
HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*
HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*
```

**מבנה InstalledApp:**
```csharp
record InstalledApp(
    string?  Guid,
    string   DisplayName,
    string   Publisher,
    string   Version,
    string?  InstallLocation,
    string   UninstallString,
    string?  QuietUninstallString,
    long     EstimatedSizeKb,
    InstallerType Type,   // MSI | Squirrel | InnoSetup | NSIS | ExeGeneric
    string?  DisplayIconPath
);
```

**זיהוי סוג installer:**

| סוג | סימן היכר |
|---|---|
| MSI | `UninstallString` מכיל `msiexec /x {GUID}` |
| Squirrel | מכיל `Update.exe --uninstall` |
| InnoSetup | `unins000.exe` קיים ב-InstallLocation |
| NSIS | uninstall.exe עם Nullsoft metadata |
| ExeGeneric | כל השאר |

---

### 6.2 Pre-Uninstall Snapshot

```csharp
// שיטה א — Registry export (מהיר, תמיד זמין)
string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
RunElevated("reg.exe", $"export HKLM\\SOFTWARE \"{snapDir}\\hklm_{ts}.reg\" /y");
RunElevated("reg.exe", $"export HKCU\\SOFTWARE \"{snapDir}\\hkcu_{ts}.reg\" /y");

// שיטה ב — VSS Shadow Copy (מלא, דורש elevation)
// IVssBackupComponents → SetBackupState → GatherWriterMetadata → DoSnapshotSet
// שמור shadow copy DeviceName ל-DB לשחזור עתידי
```

---

### 6.3 Zombie Process Handler

```csharp
// לפני הרצת uninstaller — בדוק תהליכים שנטענו מ-InstallLocation
IEnumerable<Process> GetZombies(string installLocation) =>
    Process.GetProcesses().Where(p => {
        try { return p.MainModule?.FileName
                      .StartsWith(installLocation, StringComparison.OrdinalIgnoreCase)
                      == true; }
        catch { return false; }
    });

// סגירה עדינה
foreach (var p in zombies) p.CloseMainWindow();
await Task.Delay(3000);

// אחרי אישור משתמש — כוח
foreach (var p in zombies.Where(p => !p.HasExited)) p.Kill(entireProcessTree: true);
```

---

### 6.4 Uninstall Execution

```csharp
string args = app.Type switch {
    InstallerType.MSI      => $"/x {app.Guid} /qn /norestart",
    InstallerType.Squirrel => "--uninstall --silent",
    InstallerType.InnoSetup => "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
    InstallerType.NSIS     => "/S",
    _                      => app.QuietUninstallString
                               ?? TryCommonSilentFlags(app.UninstallString)
};
string exe = app.Type == InstallerType.MSI ? "msiexec.exe"
           : ExtractExePath(app.UninstallString);
await RunAndWaitAsync(exe, args, timeout: TimeSpan.FromMinutes(10));
```

---

### 6.5 Leftover Heuristic Scanner

**Similarity Scorer:**

| קריטריון | משקל |
|---|---|
| Contains exact GUID | +0.95 |
| Path segment == DisplayName | +0.85 |
| Path segment == Publisher | +0.70 |
| Levenshtein(segment, DisplayName) < 3 | +0.50 |
| Inside InstallLocation | +0.80 |
| Creation date ≈ InstallDate ±7 days | +0.15 |

**מיקומי סריקה:**
```
קבצים:   %ProgramFiles%, %ProgramFiles(x86)%, %ProgramData%
          %APPDATA%, %LOCALAPPDATA%, %APPDATA%\..\LocalLow
ריג'יסטרי: HKLM\SOFTWARE\{Publisher|DisplayName}
            HKLM\SOFTWARE\WOW6432Node\{Publisher}
            HKCU\SOFTWARE\{Publisher}
            HKLM\SYSTEM\CurrentControlSet\Services
            HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run (שני hives)
שירותים: OpenSCManager → EnumServicesStatusEx → סנן לפי BinaryPathName
טאסקים:  ITaskService → GetTasks(TASK_ENUM_HIDDEN) → triggers
Shell:   HKCR\*\shellex\ContextMenuHandlers, HKCR\CLSID\{GUID}
```

**Shared DLL Protection:**
```csharp
bool IsSafeToDelte(string dllPath) {
    // 1. SharedDLLs ref-count
    using var key = Registry.LocalMachine.OpenSubKey(
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs");
    if ((int?)key?.GetValue(dllPath) > 1) return false;

    // 2. Loaded by other process?
    // EnumProcessModules on all processes — if found → false

    // 3. Microsoft-signed?
    var trust = WinVerifyTrust(dllPath);
    if (trust == 0 && IsPublisherMicrosoft(dllPath)) return false;

    // 4. In protected dirs
    if (dllPath.StartsWith(Environment.GetFolderPath(
            SpecialFolder.Windows), OrdinalIgnoreCase)) return false;
    return true;
}
```

---

### 6.6 Registry Cleanup

```csharp
// תמיד export לפני מחיקה
void SafeDeleteKey(RegistryKey parent, string subkeyName, string backupDir) {
    string exportPath = Path.Combine(backupDir, $"{subkeyName}_{ts}.reg");
    RunElevated("reg.exe", $"export \"{parent.Name}\\{subkeyName}\" \"{exportPath}\"");
    parent.DeleteSubKeyTree(subkeyName, throwOnMissingSubKey: false);
}

// פתח עם awareness ל-WOW64
RegistryKey Open(RegistryHive hive, string path, bool view32) =>
    RegistryKey.OpenBaseKey(hive,
        view32 ? RegistryView.Registry32 : RegistryView.Registry64)
    .OpenSubKey(path, writable: true)!;
```

**לוג ממשק — Hebrew:**
```
⏳ מריץ את ה-uninstaller הרשמי...
✓  ה-uninstaller סיים (קוד יציאה: 0)
🔍 מחפש שאריות לפי שם, GUID וספק...
📁 4 תיקיות חשודות (score ≥ 0.7)
🔑 12 מפתחות רג'יסטרי
⚙️  שירות: AdobeUpdateService
📋 משימה מתוזמנת: Adobe Acrobat Update
❓ בחר מה למחוק ← המשתמש מאשר
🗑️  מוחק קבצים ומפתחות...
✅ ניקוי הושלם — שוחרר 2.3 GB
```

---

## PHASE 7 — Driver Auditor & Startup Optimizer

### Module A: Safe Driver Auditor (ללא עדכון אוטומטי)

**גילוי נהגים:**
```csharp
// WMI — Win32_PnPSignedDriver
using var searcher = new ManagementObjectSearcher(
    "SELECT * FROM Win32_PnPSignedDriver WHERE DriverName IS NOT NULL");
foreach (ManagementObject obj in searcher.Get()) {
    string name    = (string)obj["DeviceName"];
    string version = (string)obj["DriverVersion"];
    string date    = (string)obj["DriverDate"];   // WMI date format
    bool   signed  = (bool)obj["IsSigned"];
}

// SetupAPI — P/Invoke (יותר פרטים)
[DllImport("setupapi.dll", SetLastError=true)]
static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, string? Enumerator,
    IntPtr hwndParent, uint Flags);
// DIGCF_ALLCLASSES | DIGCF_PRESENT → iterate → SPDRP_DRIVER, SPDRP_MFG
```

**Driver Rollback Snapshot (לפני כל עדכון):**
```csharp
// DISM CLI — מופעל דרך ה-service (elevation מובנה)
string dest = Path.Combine(backupRoot, $"drivers_{timestamp}");
Directory.CreateDirectory(dest);
await RunAndWaitAsync("dism.exe",
    $"/Online /Export-Driver /Destination:\"{dest}\"");
// שחזור: pnputil /add-driver "{dest}\*.inf" /subdirs /install
```

**בדיקת עדכונים — תצוגה בלבד:**
```csharp
// Windows Update Agent COM API
var session  = new UpdateSession();
var searcher = session.CreateUpdateSearcher();
// חיפוש — לא מתקין!
ISearchResult res = await Task.Run(() =>
    searcher.Search("IsInstalled=0 AND Type='Driver'"));

// מציג רשימה → המשתמש בוחר ידנית
foreach (IUpdate u in res.Updates)
    yield return new DriverUpdate(u.Title, u.Identity.UpdateID,
        u.MinDownloadSize, u.RecommendedCpuSpeed);
```

**UI:**
```
┌──────────────────────────────────────────────────────┐
│  ביקורת נהגים                                        │
│  ────────────────────────────────────────────────    │
│  NVIDIA RTX 4090        556.12   ✅ חדש              │
│  Realtek HD Audio       6.0.9421 ⚠️  עדכון זמין     │
│  Intel I219-V Ethernet  12.19.2  ✅ עדכני            │
│                                                      │
│  [📸 גבה נהגים נוכחיים]    [🔍 בדוק עדכונים]       │
│  [↩️  שחזר מגיבוי]                                  │
└──────────────────────────────────────────────────────┘
```

---

### Module B: Advanced Startup & Service Optimizer

**מקורות פריטי הפעלה:**
```
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run
HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run
HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce
%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\
%ProgramData%\Microsoft\Windows\Start Menu\Programs\Startup\
ITaskService → TASK_TRIGGER_BOOT | TASK_TRIGGER_LOGON
OpenSCManager → SERVICE_AUTO_START (non-Microsoft path)
```

**Delayed Start — 3 שיטות:**
```csharp
// שיטה 1: SERVICE_DELAYED_AUTO_START (שירותים בלבד)
SERVICE_DELAYED_AUTO_START_INFO info = new() { fDelayedAutostart = true };
ChangeServiceConfig2(hSvc, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref info);
// Windows מאחר ~2 דקות — לא מדויק

// שיטה 2: Scheduled Task עם Delay מדויק (גמישות מקסימלית)
string xml = $"""
<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <Triggers>
    <BootTrigger><Delay>PT{minutes}M</Delay></BootTrigger>
  </Triggers>
  <Actions>
    <Exec><Command>{exePath}</Command></Exec>
  </Actions>
  <Principals>
    <Principal><RunLevel>HighestAvailable</RunLevel></Principal>
  </Principals>
</Task>
""";
ITaskFolder root = taskService.GetFolder("\\");
root.RegisterTask($"DClean_Delayed_{appName}", xml,
    TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN);

// שיטה 3: הסר מ-Run key → שמור mapping ב-DClean.db
// HKCU\SOFTWARE\DClean\DelayedStartup\{AppName} = {originalCommand}|{delayMinutes}
```

---

## PHASE 8 — Performance Tuner, MFT Scanner & Debloater

### Module C: High-Performance & Low Latency Tuner

**Ultimate Performance Power Plan:**
```csharp
// GUID קבוע של Windows — Ultimate Performance
Guid ULTIMATE = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

[DllImport("powrprof.dll")]
static extern uint PowerSetActiveScheme(IntPtr RootPowerKey, ref Guid SchemeGuid);

// אם לא קיים ב-system — צור אותו:
await RunAsync("powercfg", "/duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61");
await RunAsync("powercfg", "/setactive e9a42b02-d5df-448d-aa00-03f14749eb61");
PowerSetActiveScheme(IntPtr.Zero, ref ULTIMATE);
```

**USB Selective Suspend — כיבוי:**
```csharp
// Sub-group: USB settings
Guid USB_SUBGROUP = new("2a737441-1930-4402-8d77-b2bebba308a3");
Guid USB_SS_SETTING = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");

[DllImport("powrprof.dll")]
static extern uint PowerWriteACValueIndex(IntPtr RootPowerKey,
    ref Guid SchemeGuid, ref Guid SubGroupGuid,
    ref Guid PowerSettingGuid, uint AcValueIndex);

PowerWriteACValueIndex(IntPtr.Zero, ref ULTIMATE,
    ref USB_SUBGROUP, ref USB_SS_SETTING, 0); // 0 = Disabled
// גם DCValueIndex = 0 (on battery)
```

**Core Parking — ביטול:**
```csharp
Guid PROC_SUBGROUP   = new("54533251-82be-4824-96c1-47b60b740d00");
Guid PARK_MIN_CORES  = new("0cc5b647-c1df-4637-891a-dec35c318583");
Guid PARK_MAX_CORES  = new("ea062031-0e34-4ff1-9b6d-eb1059334028");

// 100 = no parking (all cores always active)
PowerWriteACValueIndex(IntPtr.Zero, ref ULTIMATE,
    ref PROC_SUBGROUP, ref PARK_MIN_CORES, 100);
PowerWriteACValueIndex(IntPtr.Zero, ref ULTIMATE,
    ref PROC_SUBGROUP, ref PARK_MAX_CORES, 100);

// Apply changes
await RunAsync("powercfg", "/setactive e9a42b02-d5df-448d-aa00-03f14749eb61");
```

---

### Module D: Large File & Duplicate Analyzer (MFT-Based)

**MFT Direct Read — הרבה יותר מהיר מ-System.IO:**
```csharp
// פתח volume (דורש elevation)
SafeFileHandle hVol = CreateFile(@"\\.\C:",
    GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
    null, OPEN_EXISTING, 0, IntPtr.Zero);

// USN Journal enumeration
var enumData = new MFT_ENUM_DATA_V0 {
    StartFileReferenceNumber = 0,
    LowUsn = 0, HighUsn = long.MaxValue
};
// DeviceIoControl(hVol, FSCTL_ENUM_USN_DATA, ...) → USN_RECORD_V2 stream
// כל record: FileReferenceNumber, ParentRef, FileAttributes, FileName
// → בנה Dictionary<ulong, FileNode> → resolve full paths רקורסיבית
// → סנן: FileSize > 1_073_741_824L (1 GB)
```

**Duplicate Detection — xxHash64:**
```csharp
// שלב 1: group by exact size — O(n), zero I/O
var bySize = fileIndex.Values
    .GroupBy(f => f.Size)
    .Where(g => g.Count() > 1);

// שלב 2: hash ראשונים 4KB בלבד (quick pre-filter)
// NuGet: System.IO.Hashing
foreach (var file in candidates)
    file.PartialHash = await ComputeHashAsync(file.Path, length: 4096);

// שלב 3: hash מלא רק למועמדים שעברו שלב 2
foreach (var group in bySize.Where(HasPartialHashCollision))
    file.FullHash = await ComputeHashAsync(file.Path, length: -1); // כל הקובץ

// תוצאה: Dictionary<ulong, List<FileNode>> duplicateGroups
```

**Performance:** סריקת כונן 1TB — ~8–15 שניות (לעומת ~4–8 דקות עם Directory.EnumerateFiles).

---

### Module E: Windows Debloater & Telemetry Disabler

**הסרת Provisioned Appx:**
```csharp
// WinRT API — PackageManager
var pm = new PackageManager();
var packages = pm.FindProvisionedPackages();
var bloatware = packages.Where(p =>
    _bloatList.Any(b => p.Id.Name.Contains(b, OrdinalIgnoreCase)));

foreach (var pkg in bloatware) {
    StatusText = $"מסיר: {pkg.Id.Name}...";
    await pm.RemovePackageAsync(pkg.Id.FullName,
        RemovalOptions.RemoveForAllUsers).AsTask();
}
// רשימת ברירת מחדל: Xbox*, BingNews, CandyCrush, ZuneVideo,
//   MicrosoftSolitaireCollection, WindowsFeedbackHub
```

**Telemetry — Registry + Services:**
```csharp
// Diagnostic Data — הורדה למינימום
SetValue(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection",
    "AllowTelemetry", 1); // 0 = Security (Enterprise only), 1 = Basic

// DiagTrack service — disable
DisableService("DiagTrack");
DisableService("dmwappushservice");

// Windows Error Reporting
SetValue(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting",
    "Disabled", 1);

// Activity History
SetValue(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\System",
    "EnableActivityFeed", 0);

// Delivery Optimization
SetValue(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization",
    "DODownloadMode", 0);

// תמיד: backup .reg לפני כל SetValue
```

---

## PHASE 9 — Context Menu Manager, WinGet Updater & DAW Cleaner

### Module F: Context Menu Manager

**Registry paths — כל הסוגים:**
```
// Classic (Windows 10 + 11)
HKCR\*\shellex\ContextMenuHandlers\{Name}  = {CLSID}
HKCR\Directory\shellex\ContextMenuHandlers\{Name}
HKCR\Directory\Background\shellex\ContextMenuHandlers\{Name}
HKCR\Drive\shellex\ContextMenuHandlers\{Name}

// Static verb handlers
HKCR\*\shell\{Verb}\command        = "{exe}" "%1"
HKCR\Directory\shell\{Verb}\command

// Windows 11 — PackagedCom (MSIX apps)
HKCR\PackagedCom\Package\{PackageName}\Class\{CLSID}
```

**כיבוי בטוח (reversible):**
```csharp
// מוסכמה סטנדרטית: prefix "_" + suffix "_dclean"
// לפני: HKCR\*\shellex\ContextMenuHandlers\WinRAR
// אחרי: HKCR\*\shellex\ContextMenuHandlers\_WinRAR_dclean

// שמור ב-SQLite (DClean.db):
// CREATE TABLE ContextMenuBackup (
//   OriginalPath TEXT, DisabledPath TEXT,
//   CLSID TEXT, DllPath TEXT, DisabledAt TEXT)

// שחזור: rename back + delete from DB
```

**פרטי handler מ-CLSID:**
```csharp
using var clsidKey = Registry.ClassesRoot
    .OpenSubKey($@"CLSID\{clsid}\InprocServer32");
string dllPath = (string?)clsidKey?.GetValue(null) ?? "";
var info = FileVersionInfo.GetVersionInfo(dllPath);
// info.CompanyName, info.FileDescription → מוצג בטבלה
```

---

### Module G: Software Updater (WinGet Wrapper)

**IPC Architecture:**
```csharp
var psi = new ProcessStartInfo("winget.exe",
    "upgrade --all --silent --accept-source-agreements --accept-package-agreements") {
    RedirectStandardOutput = true,
    RedirectStandardError  = true,
    UseShellExecute        = false,
    CreateNoWindow         = true
};
using var proc = Process.Start(psi)!;
proc.OutputDataReceived += (_, e) => ParseLine(e.Data);
proc.ErrorDataReceived  += (_, e) => ParseError(e.Data);
proc.BeginOutputReadLine();
proc.BeginErrorReadLine();
await proc.WaitForExitAsync(ct);
```

**Parser — WinGet output → Hebrew:**
```csharp
void ParseLine(string? line) {
    if (line is null) return;
    if (line.Contains("Found"))
        AddLog($"🔍 נמצא עדכון: {ExtractAppName(line)}");
    else if (line.Contains("Downloading"))
        AddLog($"⬇️  מוריד: {ExtractAppName(line)}...");
    else if (line.Contains("Installing"))
        AddLog($"🔧 מתקין: {ExtractAppName(line)}...");
    else if (line.Contains("Successfully installed"))
        AddLog($"✅ הותקן: {ExtractAppName(line)}");
    else if (line.Contains("No applicable upgrade"))
        AddLog($"✓ עדכני: {ExtractAppName(line)}");
    else if (Regex.IsMatch(line, @"\d+%"))
        UpdateProgressBar(int.Parse(Regex.Match(line, @"(\d+)%").Groups[1].Value));
}
```

**Blacklist (לעולם לא מעדכן):**
```csharp
static readonly HashSet<string> NeverUpdate = [
    "Microsoft.DotNet", "Microsoft.VCRedist",
    "Microsoft.WindowsDesktopRuntime",
    "Microsoft.DirectX", // drivers via WU only
];
```

**UAC:** WinGet רץ בתוך ה-service (Session 0, SYSTEM) — כבר מורם, ללא UAC prompt.  
לאפליקציות שדורשות Session 1: `CreateProcessAsUser(userToken, ...)` עם token של המשתמש הפעיל.

---

### Module H: Pro-Audio & DAW Workspace Cleaner

**Standard Plugin Directories:**
```
VST2:  C:\Program Files\Steinberg\VSTPlugins\
       C:\Program Files\Common Files\Steinberg\VST2\
       HKLM\SOFTWARE\VST\VSTPluginsPath  (custom override)

VST3:  C:\Program Files\Common Files\VST3\    (spec-mandatory)

AAX:   C:\Program Files\Common Files\Avid\Audio\Plug-Ins\

CLAP:  C:\Program Files\Common Files\CLAP\
```

**Orphan Detection Logic:**
```csharp
enum PluginStatus { Valid, Orphan, Suspicious, Invalid }

PluginStatus AnalyzePlugin(string pluginPath) {
    // 1. PE header valid?
    if (!IsValidPE(pluginPath)) return PluginStatus.Invalid;

    // 2. Registry entry exists?
    bool hasReg = Registry.LocalMachine
        .OpenSubKey($@"SOFTWARE\{GuessPublisher(pluginPath)}") != null;

    // 3. Uninstaller exists?
    bool hasUninstaller = FindUninstallEntry(
        Path.GetFileNameWithoutExtension(pluginPath)) != null;

    // 4. Digitally signed?
    bool signed = VerifyTrust(pluginPath) == 0;

    if (!hasReg && !hasUninstaller && !signed) return PluginStatus.Orphan;
    if (!hasReg || !hasUninstaller)            return PluginStatus.Suspicious;
    return PluginStatus.Valid;
}

// LOAD_LIBRARY_AS_DATAFILE — בדיקה ללא הרצה
LoadLibraryEx(pluginPath, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
```

**DAW Cache Paths:**
```csharp
static readonly (string Daw, string Pattern, string Description)[] CachePaths = [
    ("Reaper",    @"**\*.reappeaks",       "Waveform peak files"),
    ("Pro Tools", @"%APPDATA%\Avid\Pro Tools\WaveformCache\**", "Waveform cache"),
    ("Cubase",    @"%APPDATA%\Steinberg\Cubase *\Cache\**",     "Cubase cache"),
    ("FL Studio", @"%APPDATA%\Image-Line\FL Studio\**\*.fsc",   "Channel state cache"),
    ("Ableton",   @"%APPDATA%\Ableton\Live *\Database\**",      "Database cache"),
    ("Bitwig",    @"%APPDATA%\Bitwig Studio\cache\**",          "Render cache"),
];
// כל אלה נוצרים מחדש אוטומטית בפתיחת הפרויקט — בטוח למחיקה מלאה
```

**Broken Sample Library Paths:**
```csharp
// Native Instruments (Kontakt, Komplete)
var kontaktLibs = Registry.CurrentUser
    .OpenSubKey(@"SOFTWARE\Native Instruments\Kontakt 7\Libraries");
foreach (string libId in kontaktLibs?.GetSubKeyNames() ?? []) {
    string? path = (string?)kontaktLibs.OpenSubKey(libId)?.GetValue("Path");
    if (path != null && !Directory.Exists(path))
        yield return new BrokenLibrary("Native Instruments", libId, path);
}

// Spectrasonics (Omnisphere, Keyscape) — SQLite DB
string specDb = Path.Combine(
    Environment.GetFolderPath(SpecialFolder.ApplicationData),
    "Spectrasonics", "libraries.db");
using var conn = new SqliteConnection($"Data Source={specDb}");
// SELECT name, path FROM libraries → validate each path

// EastWest PLAY
string ewXml = Path.Combine(AppData, "EastWest", "PLAY", "libraries.xml");
// XDocument.Load → check each <Library><Path> element
```

**UI — מנקה DAW:**
```
┌──────────────────────────────────────────────────────┐
│  מנקה סביבת עבודה אודיו                             │
│  ──────────────────────────────────────────────────  │
│  🎵 תוספי VST/VST3/AAX                              │
│     ✅ 124 תקינים  ⚠️ 3 חשודים  ❌ 1 שבור          │
│                                                      │
│  💾 Cache של DAWs                                    │
│     Reaper peak files      2.3 GB    [🗑️  מחק]     │
│     Ableton waveform cache  890 MB   [🗑️  מחק]     │
│     FL Studio channel cache  45 MB   [🗑️  מחק]     │
│                                                      │
│  🔗 ספריות סמפל — נתיבים שבורים                    │
│     Kontakt "Symphonic Winds"  →  D:\Samples\ ❌    │
│     Omnisphere "Keyscape"      →  E:\Sounds\  ❌    │
│     [✏️ עדכן נתיב]  [🗑️  הסר]                      │
└──────────────────────────────────────────────────────┘
```
