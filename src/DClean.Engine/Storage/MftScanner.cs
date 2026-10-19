using DClean.Contracts;
using DClean.Engine.Safety;
using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace DClean.Engine.Storage;

[SupportedOSPlatform("windows")]
public sealed class MftScanner(PathValidator validator, DryRunGuard dryRunGuard, ILogger<MftScanner> log)
{
    private const long DefaultLargeFileSizeBytes = 100L * 1024 * 1024; // 100 MB
    private const int  HashBufferSize             = 262_144;            // 256 KB
    private const int  HashTruncateBytes          = 10;                 // SHA-1 truncated for speed
    private const long DefaultDuplicateMinSize    = 1024 * 1024;       // 1 MB

    // ── Large file scan ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<LargeFileEntry>> ScanLargeFilesAsync(
        IReadOnlyList<string> roots,
        long minSizeBytes = DefaultLargeFileSizeBytes,
        IProgress<ScanProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<LargeFileEntry>();

        await Task.Run(() =>
        {
            foreach (string root in roots)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    progress?.Report(new ScanProgressInfo($"סורק: {root}", 0, 0));
                    ScanDirectory(root, minSizeBytes, results, progress, ct);
                }
                catch (UnauthorizedAccessException) { }
                catch (Exception ex) { log.LogDebug(ex, "Error scanning {Root}", root); }
            }
            results.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
        }, ct).ConfigureAwait(false);

        return results;
    }

    private void ScanDirectory(string dir, long minSize,
        List<LargeFileEntry> results,
        IProgress<ScanProgressInfo>? progress, CancellationToken ct)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Length >= minSize)
                    {
                        results.Add(new LargeFileEntry(
                            fi.FullName,
                            fi.Length,
                            fi.LastWriteTimeUtc,
                            fi.Extension.ToLowerInvariant()));
                        progress?.Report(new ScanProgressInfo(
                            fi.FullName, results.Count, fi.Length));
                    }
                }
                catch (Exception ex) { log.LogDebug(ex, "Skipped file in scan"); }
            }

            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                ct.ThrowIfCancellationRequested();
                try { ScanDirectory(sub, minSize, results, progress, ct); }
                catch (UnauthorizedAccessException) { }
                catch (Exception ex) { log.LogDebug(ex, "Skipped subdirectory {Dir}", sub); }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (Exception ex) { log.LogDebug(ex, "Error enumerating directory {Dir}", dir); }
    }

    // ── Duplicate detection ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(
        IReadOnlyList<string> roots,
        long minSizeBytes = DefaultDuplicateMinSize,
        IProgress<ScanProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        var groups = new List<DuplicateGroup>();

        await Task.Run(async () =>
        {
            // Step 1: group by size (quick pre-filter)
            var bySize = new Dictionary<long, List<string>>();

            progress?.Report(new ScanProgressInfo("שלב 1: סריקת גדלים...", 0, 0));
            foreach (string root in roots)
            {
                try { GatherBySize(root, minSizeBytes, bySize, ct); }
                catch (Exception ex) { log.LogDebug(ex, "Error gathering sizes in {Root}", root); }
            }

            // Step 2: hash only groups that have 2+ files
            int groupIdx  = 0;
            int sizeGroups = bySize.Count(kv => kv.Value.Count > 1);
            var byHash    = new Dictionary<string, List<string>>();

            foreach (var (size, files) in bySize.Where(kv => kv.Value.Count > 1))
            {
                ct.ThrowIfCancellationRequested();
                groupIdx++;
                int pct = (int)((float)groupIdx / sizeGroups * 100);
                progress?.Report(new ScanProgressInfo(
                    $"שלב 2: בדיקת hash ({groupIdx}/{sizeGroups})...", groupIdx, size));

                foreach (string file in files)
                {
                    try
                    {
                        string hash = await ComputeHashAsync(file, ct).ConfigureAwait(false);
                        if (!byHash.TryGetValue(hash, out var bucket))
                        {
                            bucket = new List<string>();
                            byHash[hash] = bucket;
                        }
                        bucket.Add(file);
                    }
                    catch (Exception ex) { log.LogDebug(ex, "Hash failed for {File}", file); }
                }
            }

            // Step 3: build groups
            foreach (var (hash, files) in byHash.Where(kv => kv.Value.Count > 1))
            {
                var entries = files
                    .Select(f =>
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            return new LargeFileEntry(f, fi.Length,
                                fi.LastWriteTimeUtc, fi.Extension.ToLowerInvariant());
                        }
                        catch (Exception) { return null; }
                    })
                    .Where(e => e != null)
                    .Cast<LargeFileEntry>()
                    .ToList();

                if (entries.Count > 1)
                {
                    long wastedBytes = entries.Sum(e => e.SizeBytes) - entries.Min(e => e.SizeBytes);
                    groups.Add(new DuplicateGroup(hash, entries, wastedBytes));
                }
            }

            groups.Sort((a, b) => b.WastedBytes.CompareTo(a.WastedBytes));
        }, ct).ConfigureAwait(false);

        return groups;
    }

    private void GatherBySize(string dir, long minSize,
        Dictionary<long, List<string>> bySize, CancellationToken ct)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    long len = new FileInfo(file).Length;
                    if (len >= minSize)
                    {
                        if (!bySize.TryGetValue(len, out var bucket))
                        {
                            bucket = new List<string>();
                            bySize[len] = bucket;
                        }
                        bucket.Add(file);
                    }
                }
                catch (Exception ex) { log.LogDebug(ex, "Skipped file {File}", file); }
            }

            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                ct.ThrowIfCancellationRequested();
                try { GatherBySize(sub, minSize, bySize, ct); }
                catch (Exception ex) { log.LogDebug(ex, "Skipped subdirectory {Sub}", sub); }
            }
        }
        catch (Exception ex) { log.LogDebug(ex, "Error enumerating {Dir}", dir); }
    }

    // ── Hashing ───────────────────────────────────────────────────────────────

    private static async Task<string> ComputeHashAsync(string path, CancellationToken ct)
    {
        // Use SHA-1 truncated to 10 bytes for speed (good enough for dup detection)
        using var sha    = SHA1.Create();
        await using var  fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: HashBufferSize, useAsync: true);
        var hashBytes    = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hashBytes[..HashTruncateBytes]);
    }

    // ── Safe delete ───────────────────────────────────────────────────────────

    public long DeleteFiles(IReadOnlyList<string> paths)
    {
        if (!dryRunGuard.IsExecuteMode)
        {
            log.LogWarning("DeleteFiles called but DryRunGuard is not in Execute mode — aborting");
            return 0L;
        }

        long freed = 0L;
        foreach (string path in paths)
        {
            if (validator.Validate(path, out var reason) != ValidationResult.Allowed)
            {
                log.LogWarning("DeleteFiles blocked {Path}: {Reason}", path, reason);
                continue;
            }
            try
            {
                var fi = new FileInfo(path);
                freed += fi.Length;
                fi.Delete();
                log.LogInformation("Deleted file {Path}", path);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not delete {Path}", path);
            }
        }
        return freed;
    }
}
