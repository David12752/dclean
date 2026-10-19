using DClean.UI.Services;
using DClean.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;

namespace DClean.UI;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        services.AddSingleton<IPipeClient, PipeClient>();

        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<ProcessViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<StartupViewModel>();
        services.AddSingleton<LogViewModel>();
        services.AddSingleton<UninstallerViewModel>();
        services.AddSingleton<PerformanceTunerViewModel>();
        services.AddSingleton<MftScannerViewModel>();
        services.AddSingleton<DebloaterViewModel>();
        services.AddSingleton<ContextMenuViewModel>();
        services.AddSingleton<WinGetUpdaterViewModel>();
        services.AddSingleton<DriverAuditorViewModel>();
        services.AddSingleton<DawCleanerViewModel>();
        services.AddSingleton<AudioEndpointViewModel>();
        services.AddSingleton<NetworkFlushViewModel>();
        services.AddSingleton<ComponentStoreViewModel>();
        services.AddSingleton<SmartAnalyzerViewModel>();
        services.AddSingleton<FullScanViewModel>();
        services.AddSingleton<MainViewModel>();
        Services = services.BuildServiceProvider();

        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(
                $"שגיאה בלתי צפויה:\n{ex.Exception.Message}",
                "DClean — שגיאה",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ex.Handled = true;
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Services is IDisposable d) d.Dispose();
        base.OnExit(e);
    }
}

