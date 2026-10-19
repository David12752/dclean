using DClean.Contracts;
using DClean.Engine.Safety;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace DClean.Engine.DAW;

[SupportedOSPlatform("windows")]
public sealed class DawCleaner(PathValidator validator, ILogger<DawCleaner> log)
{
    private static readonly string AppData =
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string LocalApp =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // ── Plugin directories ────────────────────────────────────────────────

    private static readonly (string Format, string Path)[] PluginDirs =
    [
        ("VST2",  @"C:\Program Files\Steinberg\VSTPlugins"),
        ("VST2",  @"C:\Program Files\Common Files\Steinberg\VST2"),
        ("VST3",  @"C:\Program Files\Common Files\VST3"),
        ("AAX",   @"C:\Program Files\Common Files\Avid\Audio\Plug-Ins"),
        ("CLAP",  @"C:\Program Files\Common Files\CLAP"),
    ];

    // ── DAW cache patterns ────────────────────────────────────────────────

    private static readonly (string Daw, string Pattern, string Description)[] CacheDefs =
    [
        ("Reaper",    "*.reappeaks",   "Waveform peak files"),
        ("Pro Tools", "*.ptx.cache",   "Pro Tools cache"),
        ("FL Studio", "*.fsc",         "FL Studio channel cache"),
    ];

    // ── Public API ────────────────────────────────────────────────────────

    public IReadOnlyList<PluginEntry> ScanPlugins()
    {
        var results = new List<PluginEntry>();
        foreach (var (format, dir) in PluginDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.*",
                    SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is not (".dll" or ".vst3" or ".aaxplugin" or ".clap")) continue;
                    results.Add(new PluginEntry(
                        Path:       file,
                        Format:     format,
                        Name:       Path.GetFileNameWithoutExtension(file),
                        SizeBytes:  new FileInfo(file).Length,
                        Status:     ClassifyPlugin(file)));
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("Cannot scan plugin dir {Dir}: {Msg}", dir, ex.Message);
            }
        }
        return results;
    }

    public IReadOnlyList<DawCacheEntry> ScanDawCaches()
    {
        var results = new List<DawCacheEntry>();

        // Dynamic cache paths per DAW
        var cachePaths = BuildCachePaths();
        foreach (var (daw, dir, desc) in cachePaths)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                long size = new DirectoryInfo(dir)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);
                if (size > 0)
                    results.Add(new DawCacheEntry(daw, dir, desc, size));
            }
            catch (Exception ex) { log.LogDebug(ex, "Error calculating cache size for {Dir}", dir); }
        }

        // File-pattern caches (scattered)
        foreach (var (daw, pattern, desc) in CacheDefs)
        {
            var roots = new[] { AppData, LocalApp,
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    var files = Directory.EnumerateFiles(root, pattern, new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible    = true
                    }).ToList();
                    long size = files.Sum(f => new FileInfo(f).Length);
                    if (size > 0)
                        results.Add(new DawCacheEntry(daw, root + "|" + pattern, desc, size)
                        { FileList = files });
                }
                catch (Exception ex) { log.LogDebug(ex, "Error scanning file-pattern cache {Pattern} in {Root}", pattern, root); }
            }
        }

        return results;
    }

    public IReadOnlyList<BrokenSampleLibrary> ScanBrokenLibraries()
    {
        var results = new List<BrokenSampleLibrary>();

        // Native Instruments (Kontakt)
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Native Instruments\Kontakt 7\Libraries");
            if (key != null)
                foreach (string libId in key.GetSubKeyNames())
                {
                    string? path = key.OpenSubKey(libId)?.GetValue("Path") as string;
                    if (path != null && !Directory.Exists(path))
                        results.Add(new BrokenSampleLibrary("Native Instruments / Kontakt", libId, path));
                }
        }
        catch (Exception ex) { log.LogDebug(ex, "Error scanning Native Instruments libraries"); }

        // Spectrasonics
        foreach (var dir in new[] { "Omnisphere", "Keyscape", "Trilian" })
        {
            var path = Path.Combine(AppData, "Spectrasonics", dir);
            if (!Directory.Exists(path)) continue;
            // placeholder: in a real implementation we'd parse libraries.db/xml
        }

        return results;
    }

    public long DeleteCacheEntries(IReadOnlyList<DawCacheEntry> entries)
    {
        long freed = 0;
        foreach (var entry in entries)
        {
            // File-pattern cache (pipe-delimited root|pattern)
            if (entry.CachePath.Contains('|'))
            {
                foreach (var file in entry.FileList ?? [])
                    freed += SafeDeleteFile(file);
            }
            else if (Directory.Exists(entry.CachePath))
            {
                foreach (var file in Directory.EnumerateFiles(
                    entry.CachePath, "*", SearchOption.AllDirectories))
                    freed += SafeDeleteFile(file);
            }
        }
        return freed;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private long SafeDeleteFile(string path)
    {
        if (validator.Validate(path, out var reason) != ValidationResult.Allowed)
        {
            log.LogWarning("DAW delete blocked {Path}: {Reason}", path, reason);
            return 0;
        }
        try
        {
            long sz = new FileInfo(path).Length;
            File.Delete(path);
            return sz;
        }
        catch (Exception ex)
        {
            log.LogWarning("Cannot delete {Path}: {Msg}", path, ex.Message);
            return 0;
        }
    }

    private static PluginStatus ClassifyPlugin(string file)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(file);
            bool hasVersion = !string.IsNullOrWhiteSpace(info.ProductName)
                           || !string.IsNullOrWhiteSpace(info.CompanyName);
            return hasVersion ? PluginStatus.Valid : PluginStatus.Suspicious;
        }
        catch (Exception) { return PluginStatus.Suspicious; }
    }

    private static IReadOnlyList<(string Daw, string Dir, string Desc)> BuildCachePaths()
    {
        var list = new List<(string, string, string)>();

        AddIfExists(list, "Pro Tools",
            Path.Combine(AppData, "Avid", "Pro Tools", "WaveformCache"), "Waveform cache");
        AddIfExists(list, "Ableton",
            Path.Combine(AppData, "Ableton"), "Database cache", isGlob: true,
            subPattern: "Live*");
        AddIfExists(list, "Bitwig",
            Path.Combine(AppData, "Bitwig Studio", "cache"), "Render cache");

        // Cubase (versioned)
        foreach (var d in Directory.GetDirectories(AppData, "Cubase*")
                     .Concat(Directory.GetDirectories(AppData, "Nuendo*")))
        {
            var cache = Path.Combine(d, "Cache");
            if (Directory.Exists(cache))
                list.Add(("Steinberg / Cubase", cache, "Cubase cache"));
        }

        return list;

        static void AddIfExists(List<(string, string, string)> l,
            string daw, string dir, string desc,
            bool isGlob = false, string? subPattern = null)
        {
            if (isGlob && subPattern != null)
            {
                if (!Directory.Exists(dir)) return;
                foreach (var sub in Directory.GetDirectories(dir, subPattern))
                {
                    var db = Path.Combine(sub, "Database");
                    if (Directory.Exists(db)) l.Add((daw, db, desc));
                }
            }
            else if (Directory.Exists(dir))
                l.Add((daw, dir, desc));
        }
    }
}
