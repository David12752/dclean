using DClean.Contracts;
using DClean.Engine.Safety;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace DClean.Engine.Cleaning;

public sealed class FileScanner
{
    private static readonly TimeSpan MinFileAge = TimeSpan.FromHours(48);

    private readonly PathValidator _validator;
    private readonly ILogger<FileScanner> _log;

    public FileScanner(PathValidator validator, ILogger<FileScanner> log)
    {
        _validator = validator;
        _log = log;
    }

    public async IAsyncEnumerable<ScanResult> ScanAsync(
        IEnumerable<string> roots,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories  = true,
            IgnoreInaccessible     = true,
            AttributesToSkip       = FileAttributes.System | FileAttributes.Device,
            ReturnSpecialDirectories = false,
        };

        foreach (var root in roots)
        {
            if (ct.IsCancellationRequested) yield break;

            IEnumerable<FileInfo> files;
            try
            {
                files = new DirectoryInfo(root).EnumerateFiles("*", options);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Cannot enumerate {Root}: {Msg}", root, ex.Message);
                continue;
            }

            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) yield break;

                // Skip reparse points (symlinks / junctions)
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    _log.LogDebug("Skipping reparse point: {Path}", file.FullName);
                    continue;
                }

                // Skip OneDrive cloud-only files to avoid triggering a download
                const FileAttributes CloudOnlyMask =
                    (FileAttributes)0x00400000 | (FileAttributes)0x00040000;
                if ((file.Attributes & CloudOnlyMask) != 0)
                    continue;

                // Skip MSI temp files (active Windows Installer staging)
                if (IsMsiTemp(file)) continue;

                // Age filter — don't touch files modified within the last 48 hours
                if (IsTooRecent(file)) continue;

                if (_validator.Validate(file.FullName, out var reason) == ValidationResult.Blocked)
                {
                    yield return new ScanResult(file.FullName, 0, Blocked: true, reason);
                    continue;
                }

                long size = 0;
                try { size = file.Length; } catch { }

                yield return new ScanResult(file.FullName, size, Blocked: false, string.Empty);

                await Task.Yield();
            }
        }
    }

    private static bool IsTooRecent(FileInfo file)
    {
        try { return DateTime.UtcNow - file.LastWriteTimeUtc < MinFileAge; }
        catch { return true; }
    }

    private static bool IsMsiTemp(FileInfo file) =>
        file.Name.StartsWith("MSI", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(file.Extension, ".tmp", StringComparison.OrdinalIgnoreCase);
}
