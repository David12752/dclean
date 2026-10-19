using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DClean.Contracts;
using DClean.UI.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace DClean.UI.ViewModels;

public sealed partial class MftScannerViewModel : ObservableObject
{
    private readonly IPipeClient _client;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool   _isScanning     = false;
    [ObservableProperty] private string _statusText     = "בחר תיקיות ולחץ סרוק";
    [ObservableProperty] private int    _progress       = 0;
    [ObservableProperty] private long   _minSizeBytes   = 100L * 1024 * 1024; // 100 MB
    [ObservableProperty] private bool   _findDuplicates = false;
    [ObservableProperty] private int    _activeTab      = 0; // 0=Large files, 1=Duplicates
    [ObservableProperty] private string _scanRoots      = string.Join(";",
        DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName));

    public ObservableCollection<LargeFileItemViewModel>  LargeFiles  { get; } = [];
    public ObservableCollection<DuplicateGroupViewModel> DupGroups   { get; } = [];
    public ObservableCollection<string>                  ScanLog     { get; } = [];

    public string MinSizeMb
    {
        get => (MinSizeBytes / 1024.0 / 1024.0).ToString("F0");
        set
        {
            if (double.TryParse(value, out double mb))
                MinSizeBytes = (long)(mb * 1024 * 1024);
        }
    }

    public MftScannerViewModel(IPipeClient client)
    {
        _client = client;
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (!await _client.EnsureConnectedAsync()) { StatusText = "לא ניתן להתחבר לשירות"; return; }

        _cts         = new CancellationTokenSource();
        IsScanning   = true;
        Progress     = 0;
        LargeFiles.Clear();
        DupGroups.Clear();
        ScanLog.Clear();
        StatusText   = "סורק...";

        try
        {
            var roots = ScanRoots.Split(';', StringSplitOptions.RemoveEmptyEntries)
                                  .Select(r => r.Trim())
                                  .Where(Directory.Exists)
                                  .ToList();

            if (roots.Count == 0) { StatusText = "לא נבחרו נתיבים תקינים"; return; }

            var req = new MftScanRequest(roots, MinSizeBytes, FindDuplicates);
            int fileCount = 0;

            await foreach (var item in _client.ScanLargeFilesAsync(req, _cts.Token))
            {
                switch (item)
                {
                    case ScanProgressInfo p:
                        fileCount = p.FilesFound;
                        StatusText = $"נמצאו {p.FilesFound} קבצים | {TruncatePath(p.CurrentPath, 60)}";
                        Progress   = Math.Min(90, Progress + 1);
                        break;

                    case MftScanResult r:
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            foreach (var f in r.LargeFiles)
                                LargeFiles.Add(new LargeFileItemViewModel(f));
                            foreach (var g in r.DuplicateGroups)
                                DupGroups.Add(new DuplicateGroupViewModel(g));
                        });
                        long totalBytes = r.LargeFiles.Sum(f => f.SizeBytes);
                        StatusText = $"הושלם — {r.LargeFiles.Count} קבצים גדולים · " +
                                     $"{r.DuplicateGroups.Count} קבוצות כפולים · " +
                                     $"{FormatHelper.FormatBytes(totalBytes)}";
                        Progress = 100;
                        break;
                }
            }
        }
        catch (OperationCanceledException) { StatusText = "סריקה בוטלה"; }
        catch (Exception ex) { StatusText = $"שגיאה: {ex.Message}"; }
        finally
        {
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanScan() => !IsScanning;

    [RelayCommand]
    private void CancelScan() => _cts?.Cancel();

    [RelayCommand]
    private async Task DeleteSelectedLargeFilesAsync()
    {
        var selected = LargeFiles.Where(f => f.IsSelected).Select(f => f.Path).ToList();
        if (selected.Count == 0) return;

        var confirm = MessageBox.Show(
            $"למחוק {selected.Count} קבצים?\nאין אפשרות לשחזור!",
            "אישור מחיקה", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        long freed = await _client.DeleteMftFilesAsync(selected);
        foreach (var vm in LargeFiles.Where(f => f.IsSelected).ToList())
            LargeFiles.Remove(vm);
        StatusText = $"נמחקו {selected.Count} קבצים, שוחרר {FormatHelper.FormatBytes(freed)}";
    }

    [RelayCommand]
    private async Task DeleteSelectedDupFilesAsync()
    {
        var toDelete = DupGroups
            .SelectMany(g => g.Files)
            .Where(f => f.IsSelected)
            .Select(f => f.Path)
            .ToList();

        if (toDelete.Count == 0) return;

        var confirm = MessageBox.Show(
            $"למחוק {toDelete.Count} קבצים כפולים?\nאין אפשרות לשחזור!",
            "אישור מחיקה", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        long freed = await _client.DeleteMftFilesAsync(toDelete);
        StatusText = $"נמחקו {toDelete.Count} קבצים, שוחרר {FormatHelper.FormatBytes(freed)}";
        // Refresh
        await ScanCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void SetActiveTab(object? param)
    {
        if (param is int tab) ActiveTab = tab;
        else if (param is string s && int.TryParse(s, out int t)) ActiveTab = t;
    }

    [RelayCommand]
    private void SelectAllLargeFiles()
    {
        foreach (var f in LargeFiles) f.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAllLargeFiles()
    {
        foreach (var f in LargeFiles) f.IsSelected = false;
    }

    private static string TruncatePath(string path, int maxLen) =>
        path.Length <= maxLen ? path : "..." + path[^(maxLen - 3)..];
}

// ── Sub-ViewModels ────────────────────────────────────────────────────────────

public sealed partial class LargeFileItemViewModel(LargeFileEntry entry) : ObservableObject
{
    public LargeFileEntry Entry    => entry;
    public string Path             => entry.Path;
    public string Name             => System.IO.Path.GetFileName(entry.Path);
    public string Directory        => System.IO.Path.GetDirectoryName(entry.Path) ?? string.Empty;
    public string SizeText         => FormatHelper.FormatBytes(entry.SizeBytes);
    public string ExtensionBadge   => entry.Extension.TrimStart('.').ToUpperInvariant();
    public string LastModified     => entry.LastModifiedUtc.ToLocalTime().ToString("dd/MM/yy HH:mm");
    public long   SizeBytes        => entry.SizeBytes;

    [ObservableProperty] private bool _isSelected = false;
}

public sealed partial class DuplicateGroupViewModel : ObservableObject
{
    public string Hash         { get; }
    public string WastedText   { get; }
    public ObservableCollection<LargeFileItemViewModel> Files { get; } = [];

    public DuplicateGroupViewModel(DuplicateGroup group)
    {
        Hash       = group.Hash[..8];
        WastedText = FormatHelper.FormatBytes(group.WastedBytes);
        foreach (var f in group.Files)
            Files.Add(new LargeFileItemViewModel(f));
        // Pre-select all but the first (keep oldest, delete the rest)
        foreach (var f in Files.Skip(1)) f.IsSelected = true;
    }
}
