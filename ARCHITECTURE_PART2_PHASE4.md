# DClean — Part 2: Granular Project Plan
## Phase 4: User Interface (Hebrew / RTL)

---

## Phase 4.1 — Root Window Configuration

The single most important RTL setting: apply `FlowDirection="RightToLeft"` to the root `Window`. This cascades down the entire visual tree automatically.

```xml
<!-- DClean.UI/Views/MainWindow.xaml -->
<Window x:Class="DClean.UI.Views.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="clr-namespace:DClean.UI.ViewModels"
        Title="DClean — ניקוי ומנהל משאבים"
        Height="720" Width="1100"
        MinHeight="600" MinWidth="900"
        FlowDirection="RightToLeft"
        FontFamily="Segoe UI"
        Background="{StaticResource BackgroundBrush}">

    <Window.DataContext>
        <vm:MainViewModel/>
    </Window.DataContext>

    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="220"/>   <!-- Navigation sidebar (right side due to RTL) -->
            <ColumnDefinition Width="*"/>      <!-- Content area -->
        </Grid.ColumnDefinitions>

        <!-- Navigation -->
        <Border Grid.Column="0" Background="{StaticResource SidebarBrush}">
            <ItemsControl ItemsSource="{Binding NavigationItems}">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <Button Content="{Binding Label}"
                                Command="{Binding NavigateCommand}"
                                Style="{StaticResource NavButtonStyle}"/>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </Border>

        <!-- Main content (frame-based navigation) -->
        <Frame Grid.Column="1" x:Name="ContentFrame"
               NavigationUIVisibility="Hidden"/>
    </Grid>
</Window>
```

### RTL-Specific XAML Notes

1. **`FlowDirection="RightToLeft"` on `Window`** — cascades to all children. No per-control setting needed.
2. **Scroll bars** automatically appear on the left side in RTL mode.
3. **`Grid` columns** are logically reversed: Column 0 is visually on the right, Column 1 is on the left.
4. **Icons that have directional meaning** (arrows, progress indicators) must be mirrored. Use `ScaleTransform(ScaleX=-1)` or provide dedicated RTL assets.
5. **`TextAlignment="Right"` is the default** for RTL text — do not override it with `Left` unless intentional.
6. **Hebrew font stack:** `Segoe UI` (Windows 10+) has excellent Hebrew coverage. Fall back to `Arial` for older systems. The `FontFamily` on the root `Window` cascades.

---

## Phase 4.2 — Theme / Resource Dictionary

```xml
<!-- DClean.UI/Themes/DCleanTheme.xaml -->
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <!-- Colors -->
    <Color x:Key="PrimaryColor">#0078D4</Color>      <!-- Microsoft Fluent Blue -->
    <Color x:Key="DangerColor">#D13438</Color>        <!-- Red for destructive actions -->
    <Color x:Key="SuccessColor">#107C10</Color>       <!-- Green for completed -->
    <Color x:Key="WarningColor">#FFB900</Color>       <!-- Amber for dry-run -->
    <Color x:Key="BackgroundColor">#1E1E2E</Color>    <!-- Dark background -->
    <Color x:Key="SurfaceColor">#2A2A3C</Color>
    <Color x:Key="SidebarColor">#252538</Color>
    <Color x:Key="ForegroundColor">#F0F0F0</Color>

    <SolidColorBrush x:Key="PrimaryBrush"    Color="{StaticResource PrimaryColor}"/>
    <SolidColorBrush x:Key="DangerBrush"     Color="{StaticResource DangerColor}"/>
    <SolidColorBrush x:Key="SuccessBrush"    Color="{StaticResource SuccessColor}"/>
    <SolidColorBrush x:Key="WarningBrush"    Color="{StaticResource WarningColor}"/>
    <SolidColorBrush x:Key="BackgroundBrush" Color="{StaticResource BackgroundColor}"/>
    <SolidColorBrush x:Key="SurfaceBrush"    Color="{StaticResource SurfaceColor}"/>
    <SolidColorBrush x:Key="SidebarBrush"    Color="{StaticResource SidebarColor}"/>
    <SolidColorBrush x:Key="ForegroundBrush" Color="{StaticResource ForegroundColor}"/>

    <!-- Modern Button Style -->
    <Style x:Key="PrimaryButtonStyle" TargetType="Button">
        <Setter Property="Background"  Value="{StaticResource PrimaryBrush}"/>
        <Setter Property="Foreground"  Value="White"/>
        <Setter Property="Padding"     Value="16,8"/>
        <Setter Property="FontSize"    Value="14"/>
        <Setter Property="FontWeight"  Value="SemiBold"/>
        <Setter Property="Cursor"      Value="Hand"/>
        <Setter Property="BorderThickness" Value="0"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border Background="{TemplateBinding Background}"
                            CornerRadius="6"
                            Padding="{TemplateBinding Padding}">
                        <ContentPresenter HorizontalAlignment="Center"
                                          VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter Property="Background" Value="#106EBE"/>
                        </Trigger>
                        <Trigger Property="IsPressed" Value="True">
                            <Setter Property="Background" Value="#005A9E"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Opacity" Value="0.5"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Danger Button (for destructive Execute actions) -->
    <Style x:Key="DangerButtonStyle" TargetType="Button"
           BasedOn="{StaticResource PrimaryButtonStyle}">
        <Setter Property="Background" Value="{StaticResource DangerBrush}"/>
    </Style>

    <!-- Card/Panel style -->
    <Style x:Key="CardStyle" TargetType="Border">
        <Setter Property="Background"       Value="{StaticResource SurfaceBrush}"/>
        <Setter Property="CornerRadius"     Value="10"/>
        <Setter Property="Padding"          Value="20"/>
        <Setter Property="Margin"           Value="8"/>
    </Style>

</ResourceDictionary>
```

---

## Phase 4.3 — Dashboard ViewModel (MVVM)

```csharp
// DClean.UI/ViewModels/DashboardViewModel.cs

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly ICleaningServiceClient _client;
    private CancellationTokenSource? _scanCts;

    [ObservableProperty] private string _statusText = "מוכן לסריקה";
    [ObservableProperty] private double _scanProgress = 0;
    [ObservableProperty] private bool _isScanning = false;
    [ObservableProperty] private bool _isDryRun = true;           // Default: dry run
    [ObservableProperty] private long _totalBytesFound = 0;
    [ObservableProperty] private long _totalBytesFreed = 0;
    [ObservableProperty] private string _ramUsageText = "---";
    [ObservableProperty] private double _ramUsagePercent = 0;

    // Scan results for the list view
    public ObservableCollection<ScanResultViewModel> ScanResults { get; } = new();

    public DashboardViewModel(ICleaningServiceClient client)
    {
        _client = client;
    }

    [RelayCommand]
    private async Task StartScanAsync()
    {
        ScanResults.Clear();
        TotalBytesFound = 0;
        IsScanning = true;
        StatusText = "סורק קבצים זמניים...";
        ScanProgress = 0;

        _scanCts = new CancellationTokenSource();

        try
        {
            await foreach (var result in _client.ScanAsync(IsDryRun, _scanCts.Token))
            {
                // ── UI thread update via App.Current.Dispatcher ──────────────
                App.Current.Dispatcher.Invoke(() =>
                {
                    ScanResults.Add(new ScanResultViewModel(result));
                    if (!result.WasSkipped)
                        TotalBytesFound += result.BytesFound;

                    // Simulate rolling progress (real progress requires counting total files first)
                    ScanProgress = Math.Min(99, ScanProgress + 0.1);
                });
            }

            ScanProgress = 100;
            StatusText = IsDryRun
                ? $"סריקת בדיקה הושלמה — נמצאו {FormatBytes(TotalBytesFound)} לניקוי"
                : $"ניקוי הושלם — שוחרר {FormatBytes(TotalBytesFreed)}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "הסריקה בוטלה";
        }
        finally
        {
            IsScanning = false;
            _scanCts.Dispose();
            _scanCts = null;
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    [RelayCommand]
    private async Task ExecuteCleaningAsync()
    {
        // Two-step confirmation for destructive operation
        var dialog = new ConfirmDialog(
            title: "אישור מחיקה",
            message: $"האם אתה בטוח שברצונך למחוק {FormatBytes(TotalBytesFound)} של קבצים?\n" +
                     "פעולה זו אינה הפיכה.");

        if (dialog.ShowDialog() != true) return;

        IsDryRun = false;
        await StartScanAsync(); // Re-runs in Execute mode
        IsDryRun = true;        // Reset to safe default after completion
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024               => $"{bytes} B",
        < 1024 * 1024        => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _                    => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}
```

**Using `CommunityToolkit.Mvvm`** (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`) — the source generator eliminates 90% of MVVM boilerplate.

---

## Phase 4.4 — Dashboard View (Hebrew Labels)

```xml
<!-- DClean.UI/Views/DashboardView.xaml -->
<UserControl x:Class="DClean.UI.Views.DashboardView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <ScrollViewer VerticalScrollBarVisibility="Auto">
        <StackPanel Margin="20">

            <!-- ── Header ──────────────────────────────────────────── -->
            <TextBlock Text="לוח בקרה"
                       FontSize="24" FontWeight="Bold"
                       Foreground="{StaticResource ForegroundBrush}"
                       Margin="0,0,0,20"/>

            <!-- ── RAM Status Card ─────────────────────────────────── -->
            <Border Style="{StaticResource CardStyle}">
                <StackPanel>
                    <TextBlock Text="זיכרון RAM" FontSize="16" FontWeight="SemiBold"
                               Foreground="{StaticResource ForegroundBrush}"/>
                    <Grid Margin="0,12,0,0">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="Auto"/>
                        </Grid.ColumnDefinitions>
                        <ProgressBar Grid.Column="0"
                                     Value="{Binding RamUsagePercent}"
                                     Maximum="100" Height="16"
                                     Foreground="{StaticResource PrimaryBrush}"/>
                        <TextBlock Grid.Column="1"
                                   Text="{Binding RamUsageText}"
                                   Foreground="{StaticResource ForegroundBrush}"
                                   Margin="12,0,0,0" VerticalAlignment="Center"/>
                    </Grid>
                </StackPanel>
            </Border>

            <!-- ── Scan Controls Card ─────────────────────────────── -->
            <Border Style="{StaticResource CardStyle}">
                <StackPanel>
                    <TextBlock Text="ניקוי קבצים" FontSize="16" FontWeight="SemiBold"
                               Foreground="{StaticResource ForegroundBrush}"/>

                    <!-- Dry-Run toggle -->
                    <StackPanel Orientation="Horizontal" Margin="0,12,0,12">
                        <CheckBox IsChecked="{Binding IsDryRun}"
                                  IsEnabled="{Binding IsScanning, Converter={StaticResource InverseBool}}"
                                  Foreground="{StaticResource ForegroundBrush}"
                                  FontSize="13"/>
                        <TextBlock Text="מצב בדיקה (ללא מחיקה בפועל)"
                                   Foreground="{StaticResource WarningBrush}"
                                   Margin="8,0,0,0" VerticalAlignment="Center" FontSize="13"/>
                    </StackPanel>

                    <!-- Progress bar -->
                    <ProgressBar Value="{Binding ScanProgress}"
                                 Maximum="100" Height="8"
                                 Margin="0,0,0,8"
                                 Foreground="{StaticResource PrimaryBrush}"
                                 Visibility="{Binding IsScanning,
                                     Converter={StaticResource BoolToVisibility}}"/>

                    <!-- Status text -->
                    <TextBlock Text="{Binding StatusText}"
                               Foreground="{StaticResource ForegroundBrush}"
                               FontSize="13" Margin="0,0,0,12"/>

                    <!-- Buttons -->
                    <StackPanel Orientation="Horizontal">
                        <Button Content="התחל סריקה"
                                Command="{Binding StartScanCommand}"
                                Style="{StaticResource PrimaryButtonStyle}"
                                Margin="0,0,8,0"/>
                        <Button Content="בצע ניקוי"
                                Command="{Binding ExecuteCleaningCommand}"
                                Style="{StaticResource DangerButtonStyle}"
                                IsEnabled="{Binding IsDryRun,
                                    Converter={StaticResource InverseBool}}"
                                Margin="0,0,8,0"/>
                        <Button Content="בטל"
                                Command="{Binding CancelScanCommand}"
                                IsEnabled="{Binding IsScanning}"
                                Margin="0,0,0,0"/>
                    </StackPanel>

                    <!-- Summary stats -->
                    <StackPanel Orientation="Horizontal" Margin="0,16,0,0">
                        <TextBlock Foreground="{StaticResource ForegroundBrush}" FontSize="13">
                            <Run Text="נמצאו: "/>
                            <Run Text="{Binding TotalBytesFound, Converter={StaticResource BytesFormat}}"
                                 FontWeight="Bold" Foreground="{StaticResource WarningBrush}"/>
                        </TextBlock>
                        <TextBlock Margin="24,0,0,0" Foreground="{StaticResource ForegroundBrush}" FontSize="13">
                            <Run Text="שוחרר: "/>
                            <Run Text="{Binding TotalBytesFreed, Converter={StaticResource BytesFormat}}"
                                 FontWeight="Bold" Foreground="{StaticResource SuccessBrush}"/>
                        </TextBlock>
                    </StackPanel>
                </StackPanel>
            </Border>

            <!-- ── Scan Results List ──────────────────────────────── -->
            <Border Style="{StaticResource CardStyle}">
                <StackPanel>
                    <TextBlock Text="תוצאות הסריקה" FontSize="16" FontWeight="SemiBold"
                               Foreground="{StaticResource ForegroundBrush}"
                               Margin="0,0,0,12"/>
                    <ListView ItemsSource="{Binding ScanResults}"
                              MaxHeight="300"
                              Background="Transparent" BorderThickness="0">
                        <ListView.ItemTemplate>
                            <DataTemplate>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="*"/>
                                        <ColumnDefinition Width="80"/>
                                        <ColumnDefinition Width="60"/>
                                    </Grid.ColumnDefinitions>
                                    <!-- Path (truncated on left in RTL = truncated at start = correct) -->
                                    <TextBlock Grid.Column="0"
                                               Text="{Binding DisplayPath}"
                                               TextTrimming="CharacterEllipsis"
                                               Foreground="{Binding StatusBrush}"
                                               FontSize="12"/>
                                    <!-- Size -->
                                    <TextBlock Grid.Column="1"
                                               Text="{Binding SizeDisplay}"
                                               Foreground="{StaticResource ForegroundBrush}"
                                               FontSize="12" TextAlignment="Center"/>
                                    <!-- Status badge -->
                                    <Border Grid.Column="2"
                                            Background="{Binding BadgeBrush}"
                                            CornerRadius="4" Padding="4,2">
                                        <TextBlock Text="{Binding StatusLabel}"
                                                   Foreground="White" FontSize="10"
                                                   TextAlignment="Center"/>
                                    </Border>
                                </Grid>
                            </DataTemplate>
                        </ListView.ItemTemplate>
                    </ListView>
                </StackPanel>
            </Border>

        </StackPanel>
    </ScrollViewer>
</UserControl>
```

---

## Phase 4.5 — Async UI Threading Pattern

WPF's `Dispatcher` must be used for all UI updates from background threads. The pattern used throughout DClean:

```csharp
// Pattern A: Dispatcher.Invoke (synchronous, use sparingly — can deadlock)
App.Current.Dispatcher.Invoke(() => ScanResults.Add(item));

// Pattern B: Dispatcher.InvokeAsync (preferred — non-blocking fire-and-forget)
await App.Current.Dispatcher.InvokeAsync(() =>
{
    ScanProgress = newValue;
    StatusText = newStatus;
});

// Pattern C: IProgress<T> (cleanest — decouples engine from UI thread details)
// Create in ViewModel (UI thread), pass to engine:
var progress = new Progress<ScanResult>(result =>
{
    // This lambda is always invoked on the UI thread (captured SynchronizationContext)
    ScanResults.Add(new ScanResultViewModel(result));
    TotalBytesFound += result.BytesFound;
});
await _client.ScanAsync(progress, _scanCts.Token);
```

**Use Pattern C (`IProgress<T>`) as the default.** It is thread-safe by design, captures the `SynchronizationContext` at construction time (always the UI thread when created in the ViewModel), and decouples the engine from any WPF-specific types.

---

## Phase 4.6 — Navigation Structure (Hebrew Labels)

```
┌─────────────────────────────────────────────────────┐
│  DClean            ←  Window Title (RTL: right-anchored) │
├──────────────┬──────────────────────────────────────┤
│  לוח בקרה   │                                        │
│  (Dashboard) │         Content Area                  │
│  ────────    │                                        │
│  ניקוי       │                                        │
│  (Cleaning)  │                                        │
│  ────────    │                                        │
│  תהליכים     │                                        │
│  (Processes) │                                        │
│  ────────    │                                        │
│  הגדרות      │                                        │
│  (Settings)  │                                        │
└──────────────┴──────────────────────────────────────┘
  ↑ Sidebar is Grid Column 0, which appears on the RIGHT in RTL
```

Navigation items and their Hebrew labels:

| Page | Hebrew | Notes |
|---|---|---|
| Dashboard | לוח בקרה | RAM meter, quick-scan button |
| Cleaning | ניקוי קבצים | File scanner, cache locator, results |
| Processes | ניהול תהליכים | Process list, working-set trim, terminate |
| Startup | פריטי הפעלה | Review startup entries (read-only) |
| Settings | הגדרות | Age threshold, scan scope, language |
| Log | יומן פעולות | ETW log viewer, audit trail |
