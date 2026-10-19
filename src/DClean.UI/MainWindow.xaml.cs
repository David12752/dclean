using DClean.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;

namespace DClean.UI;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<MainViewModel>();
    }
}