using DClean.Contracts;
using Microsoft.Extensions.Logging;

namespace DClean.Engine.Cleaning;

public sealed class BrowserCacheLocator
{
    private readonly string _localAppData;
    private readonly string _roamingAppData;
    private readonly ILogger<BrowserCacheLocator> _log;

    public BrowserCacheLocator(ILogger<BrowserCacheLocator> log)
    {
        _log = log;
        _localAppData  = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    }

    public IReadOnlyList<CacheTarget> Discover()
    {
        var targets = new List<CacheTarget>();

        var candidates = new (string Browser, string SubPath, bool IsRoaming)[]
        {
            ("Google Chrome",  @"Google\Chrome\User Data\Default\Cache",       false),
            ("Google Chrome",  @"Google\Chrome\User Data\Default\Code Cache",  false),
            ("Google Chrome",  @"Google\Chrome\User Data\Default\GPUCache",    false),
            ("MS Edge",        @"Microsoft\Edge\User Data\Default\Cache",      false),
            ("MS Edge",        @"Microsoft\Edge\User Data\Default\Code Cache", false),
            ("Brave",          @"BraveSoftware\Brave-Browser\User Data\Default\Cache", false),
            ("Opera",          @"Opera Software\Opera Stable\Cache",           false),
            ("Vivaldi",        @"Vivaldi\User Data\Default\Cache",             false),
        };

        foreach (var (browser, subpath, isRoaming) in candidates)
        {
            var root = isRoaming ? _roamingAppData : _localAppData;
            var full = Path.Combine(root, subpath);
            if (Directory.Exists(full))
            {
                targets.Add(new CacheTarget(browser, full, EstimateSize(full)));
                _log.LogDebug("Found cache: {Browser} at {Path}", browser, full);
            }
        }

        // Firefox: profile-based enumeration
        DiscoverFirefoxCaches(targets);

        return targets;
    }

    private void DiscoverFirefoxCaches(List<CacheTarget> targets)
    {
        var profilesRoot = Path.Combine(_roamingAppData, "Mozilla", "Firefox", "Profiles");
        if (!Directory.Exists(profilesRoot)) return;

        try
        {
            foreach (var profile in Directory.GetDirectories(profilesRoot))
            {
                var cachePath = Path.Combine(
                    _localAppData, "Mozilla", "Firefox", "Profiles",
                    Path.GetFileName(profile), "cache2");

                if (Directory.Exists(cachePath))
                {
                    targets.Add(new CacheTarget("Firefox", cachePath, EstimateSize(cachePath)));
                    _log.LogDebug("Found Firefox cache at {Path}", cachePath);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Firefox cache discovery failed: {Msg}", ex.Message);
        }
    }

    private long EstimateSize(string dir)
    {
        try
        {
            return new DirectoryInfo(dir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => { try { return f.Length; } catch (Exception) { return 0L; } });
        }
        catch (Exception ex) { _log.LogDebug(ex, "Could not estimate size of {Dir}", dir); return -1; }
    }
}
