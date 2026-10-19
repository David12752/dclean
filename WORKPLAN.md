# DClean — תוכנית עבודה מלאה לשדרוג האפליקציה

**תאריך:** 5 באפריל 2026  
**מבוסס על:** ביקורת מקצועית מקיפה (דוח 3 באפריל 2026)  
**סטטוס כללי:** Beta — מבנה טוב, פערים קריטיים בבדיקות, נגישות ועיצוב

---

## סיכום ממצאים

| קטגוריה | ציון | קריטי | גבוה | בינוני | נמוך |
|----------|------|--------|------|--------|------|
| אבטחה | B+ | 0 | 1 | 2 | 1 |
| תקינות ואמינות | B− | 0 | 3 | 3 | 2 |
| ביצועים | B | 0 | 1 | 2 | 1 |
| ממשק משתמש | C+ | 0 | 2 | 2 | 1 |
| ארכיטקטורה | B+ | 0 | 1 | 2 | 1 |
| בדיקות | D | 1 | 2 | 0 | 0 |
| איכות קוד | B | 0 | 1 | 2 | 1 |

**סה"כ: 3 קריטיים, 11 גבוהים, 11 בינוניים, 7 נמוכים**

---

## שלב 1: תיקוני אבטחה ותקינות קריטיים (P0)

**משך משוער:** 2-3 ימים  
**מצב:** ✅ הושלם

### משימות

- [x] **1.1** הפיכת `DryRunGuard` ל-thread-safe — עטיפת `_mode` ו-`_confirmationToken` ב-`lock`
  - קובץ: `src/DClean.Engine/Safety/DryRunGuard.cs`
  - חומרה: גבוה (C-3)

- [x] **1.2** הוספת בדיקת `DryRunGuard.IsExecuteMode` ב-`MftScanner.DeleteFiles()`
  - קובץ: `src/DClean.Engine/Storage/MftScanner.cs`
  - חומרה: גבוה (S-1)

- [x] **1.3** החלפת `_writeLock.Wait()` ב-async-safe pattern ב-`PipeServer.SafeProgress()`
  - קובץ: `src/DClean.Service/PipeServer.cs`
  - חומרה: גבוה (C-1)

- [x] **1.4** הוספת ולידציית נתיבי רג'סטרי ב-`ContextMenuEngine`
  - קובץ: `src/DClean.Engine/Shell/ContextMenuEngine.cs`
  - חומרה: בינוני (S-2)

- [x] **1.5** הוספת `ConfigureAwait(false)` בכל ספריית Engine
  - קבצים: כל `src/DClean.Engine/**/*.cs` (58 אתרים)
  - חומרה: גבוה (C-2)

### קריטריוני סיום
- כל 65+ הבדיקות הקיימות עוברות
- Build ללא שגיאות
- אין regression בפונקציונליות

---

## שלב 2: הרחבת כיסוי בדיקות (P0)

**משך משוער:** 4-5 ימים  
**מצב:** 🔄 בביצוע

### משימות

- [x] **2.1** יצירת `IPipeClient` interface — חילוץ מה-concrete class
  - קבצים: `src/DClean.UI/Services/PipeClient.cs`, חדש: `IPipeClient.cs`

- [x] **2.2** רישום כל ה-ViewModels ב-DI container
  - קבצים: `src/DClean.UI/App.xaml.cs`, `src/DClean.UI/ViewModels/MainViewModel.cs`

- [x] **תוספת ביניים** בדיקת unit ל-`ProcessViewModel` עם mock `IPipeClient`
  - קובץ חדש: `tests/DClean.Engine.Tests/ProcessViewModelTests.cs`

- [ ] **2.3** בדיקות unit ל-SafeDeleter
  - קובץ חדש: `tests/DClean.Engine.Tests/SafeDeleterTests.cs`

- [ ] **2.4** בדיקות unit ל-SafeUninstaller
  - קובץ חדש: `tests/DClean.Engine.Tests/SafeUninstallerTests.cs`

- [ ] **2.5** בדיקות unit ל-FileScanner
  - קובץ חדש: `tests/DClean.Engine.Tests/FileScannerTests.cs`

- [ ] **2.6** בדיקות unit ל-LeftoverScanner
  - קובץ חדש: `tests/DClean.Engine.Tests/LeftoverScannerTests.cs`

- [ ] **2.7** בדיקות unit ל-DashboardViewModel (עם mock IPipeClient)
  - קובץ חדש: `tests/DClean.UI.Tests/DashboardViewModelTests.cs`

- [ ] **2.8** בדיקות אינטגרציה PipeServer ↔ PipeClient
  - קובץ חדש: `tests/DClean.Integration.Tests/PipeIntegrationTests.cs`

### קריטריוני סיום
- 80+ בדיקות
- כיסוי 60%+ על קוד קריטי (Safety, Cleaning, Uninstaller)
- `dotnet test` עובר ב-CI

---

## שלב 3: תיקוני תקינות UI ושיפור קוד (P1)

**משך משוער:** 2-3 ימים  
**מצב:** ✅ הושלם

### משימות

- [x] **3.1** חילוץ `FormatBytes()` למחלקת utility משותפת + הסרת 12 שכפולים
  - קובץ חדש: `src/DClean.Contracts/FormatHelper.cs`
  - קבצים מושפעים: 10 ViewModels + PipeServer

- [x] **3.2** תיקון AudioEndpointView — שינוי שם פקודות ל-device-agnostic (`AllowAccess`/`BlockAccess`)
  - קבצים: `AudioEndpointView.xaml`, `AudioEndpointViewModel.cs`

- [x] **3.3** מימוש `SettingsViewModel.Save()` — שמירה ל-JSON ב-LocalAppData
  - קובץ: `src/DClean.UI/ViewModels/SettingsViewModel.cs`

- [x] **3.4** מימוש `LogViewModel.Export()` — ייצוא ל-CSV על שולחן העבודה
  - קובץ: `src/DClean.UI/ViewModels/LogViewModel.cs`

- [x] **3.5** החלפת 52 catch ריקים — הוספת `log.LogDebug()` ב-14 קבצים
  - קבצים: StartupEngine, MftScanner, SafeUninstaller, DawCleaner, PerformanceTuner, LeftoverScanner, ContextMenuEngine, ComponentStoreEngine, WinGetUpdater, BrowserCacheLocator, DriverAuditor, SafeTerminator, SmartAnalyzer, AudioEndpointEngine

- [x] **3.6** חילוץ מספרים קסומים לקבועים (17 מספרים ב-6 קבצים)
  - קבצים: FullScanViewModel, PerformanceTuner, StartupEngine, MftScanner, NetworkFlusher

- [x] **3.7** החלפת פרסור Regex ב-System.Text.Json ב-SmartAnalyzer
  - קובץ: `src/DClean.Engine/Storage/SmartAnalyzer.cs`

- [x] **3.8** אופטימיזציית `Levenshtein()` — ArrayPool two-row במקום מטריצה מלאה
  - קובץ: `src/DClean.Engine/Uninstaller/LeftoverScanner.cs`

### קריטריוני סיום
- אפס שכפולי FormatBytes
- Settings נשמרות ונטענות
- Export log עובד
- Build + כל הבדיקות עוברות

---

## שלב 4: שדרוג Theme ותשתית עיצובית (P1)

**משך משוער:** 3-4 ימים  
**מצב:** ⏳ ממתין

### משימות

- [ ] **4.1** הטמעת גופן ייחודי (Instrument Sans / Plus Jakarta Sans)
  - קבצים: embedded `.ttf`, DCleanTheme.xaml, MainWindow.xaml

- [ ] **4.2** איחוד 60+ צבעים מקודדים למשאבי Theme
  - קובץ: DCleanTheme.xaml + כל 18 Views

- [ ] **4.3** הוספת סגנונות חסרים: Expander, ListBox, ToolTip, ComboBox
  - קובץ: DCleanTheme.xaml

- [ ] **4.4** אנימציית מעבר דפים (fade/slide Storyboard)
  - קובץ: MainWindow.xaml

- [ ] **4.5** אנימציות כניסה לכרטיסים (staggered Loaded triggers)
  - קבצים: DashboardView, FullScanView, PerformanceTunerView

- [ ] **4.6** Micro-interactions — hover scale/glow על כרטיסים וכפתורים
  - קובץ: DCleanTheme.xaml

### קריטריוני סיום
- טיפוגרפיה ייחודית בכל המסכים
- אפס צבעים hardcoded (הכל דרך theme resources)
- אנימציות חלקות 60fps
- עמידה ב-SKILL.md guidelines

---

## שלב 5: נגישות (Accessibility) (P1)

**משך משוער:** 2-3 ימים  
**מצב:** ⏳ ממתין

### משימות

- [ ] **5.1** הוספת `AutomationProperties.Name` לכל פקד אינטראקטיבי
  - קבצים: כל 18 Views + MainWindow.xaml

- [ ] **5.2** הגדרת `KeyboardNavigation.TabNavigation` ב-sidebar ובכל View
  - קבצים: MainWindow.xaml + Views

- [ ] **5.3** הוספת `AccessKey` לכפתורי פעולה ראשיים
  - קבצים: כל Views

- [ ] **5.4** בדיקת ניגודיות WCAG AA (4.5:1)
  - קבצים: DCleanTheme.xaml

### קריטריוני סיום
- ניווט מקלדת מלא דרך כל המסכים
- Narrator קורא כל פקד
- ניגודיות WCAG AA

---

## שלב 6: Refactoring ארכיטקטוני (P2)

**משך משוער:** 3-4 ימים  
**מצב:** ⏳ ממתין

### משימות

- [ ] **6.1** Refactor PipeServer — Mediator pattern עם `IMessageHandler<TReq, TRes>`
  - קבצים: PipeServer.cs, חדש: Handlers/*.cs

- [ ] **6.2** דדופליקציית Sidebar — ItemsControl data-driven
  - קובץ: MainWindow.xaml, MainViewModel.cs

- [ ] **6.3** הוספת קבצי `.resx` לתמיכה בלוקליזציה
  - קבצים חדשים: Resources/*.resx

- [ ] **6.4** העברת DataTemplates מ-MainWindow ל-App.xaml
  - קבצים: MainWindow.xaml, App.xaml

- [ ] **6.5** החלפת ViewModel-driven brushes ב-converters
  - קבצים: ViewModels + Converters.cs

### קריטריוני סיום
- PipeServer < 100 שורות (dispatch בלבד)
- Sidebar מוגדר ב-data, לא ב-XAML
- כל DataTemplates ב-App.xaml

---

## ציר זמן

```
שבוע 1:  שלב 1 (אבטחה) + שלב 3 (תיקוני קוד)
שבוע 2:  שלב 2 (בדיקות)
שבוע 3:  שלב 4 (עיצוב) + שלב 5 (נגישות)
שבוע 4:  שלב 6 (ארכיטקטורה) + QA סופי
```

**סה"כ: ~16-22 ימי עבודה (3-4 שבועות)**

---

## הערות חשובות

1. **שלבים 1-2 הם חובה לפני release** — אין לדלג
2. **כל שינוי חייב לעבור build + tests** לפני merge
3. **שלב 4 (עיצוב) צריך להתבסס על SKILL.md** בתיקיית frontend-design
4. **שלב 5 (נגישות) נדרש עפ"י Microsoft Accessibility Standards**
