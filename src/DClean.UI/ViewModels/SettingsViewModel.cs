using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.IO;
using System.Text.Json;

namespace DClean.UI.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DClean", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [ObservableProperty] private int    _minFileAgeHours  = 48;
    [ObservableProperty] private bool   _scanWindowsTemp  = true;
    [ObservableProperty] private bool   _scanBrowserCaches = true;
    [ObservableProperty] private bool   _scanUserTemp     = true;

    public SettingsViewModel()
    {
        Load();
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var data = new SettingsData(MinFileAgeHours, ScanWindowsTemp, ScanBrowserCaches, ScanUserTemp);
            string json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch { /* UI should not crash on settings save failure */ }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            string json = File.ReadAllText(SettingsPath);
            var data = JsonSerializer.Deserialize<SettingsData>(json, JsonOptions);
            if (data is null) return;

            MinFileAgeHours   = data.MinFileAgeHours;
            ScanWindowsTemp   = data.ScanWindowsTemp;
            ScanBrowserCaches = data.ScanBrowserCaches;
            ScanUserTemp      = data.ScanUserTemp;
        }
        catch { /* Use defaults if settings file is corrupt */ }
    }

    private sealed record SettingsData(
        int  MinFileAgeHours,
        bool ScanWindowsTemp,
        bool ScanBrowserCaches,
        bool ScanUserTemp);
}
