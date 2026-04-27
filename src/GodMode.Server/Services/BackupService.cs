using System.Formats.Tar;
using System.IO.Compression;
using GodMode.Server.Models;
using Microsoft.Extensions.Options;

namespace GodMode.Server.Services;

/// <summary>
/// Creates and restores backup archives of the project roots directory.
///
/// A backup is a gzip-compressed tar archive that captures everything under
/// <c>ProjectRootsDir</c>: profiles (.profiles/), webhooks (.webhooks/),
/// data-protection keys (.godmode-keys/), and every project root with its
/// chat history (.godmode/{status,input,output,session-id,settings}).
///
/// Archives are written to and read from <c>Backup:Location</c>, which is
/// expected to be a shared/remote location mounted on the server
/// (Azure Files, NFS, EFS, S3 via FUSE, etc.).
/// </summary>
public sealed class BackupService
{
    private const string TimestampFormat = "yyyy-MM-dd_HHmmss";

    private readonly string _projectRootsDir;
    private readonly BackupConfig _config;
    private readonly ILogger<BackupService> _logger;

    public BackupService(
        IConfiguration configuration,
        IOptions<BackupConfig> options,
        ILogger<BackupService> logger)
    {
        _projectRootsDir = Path.GetFullPath(configuration["ProjectRootsDir"] ?? "roots");
        _config = options.Value;
        _logger = logger;
    }

    /// <summary>The configured shared backup location, or null if not set.</summary>
    public string? Location => _config.Location;

    /// <summary>Throws <see cref="InvalidOperationException"/> if no location is configured.</summary>
    public string RequireLocation()
    {
        if (string.IsNullOrWhiteSpace(_config.Location))
            throw new InvalidOperationException(
                "Backup location is not configured. Set Backup:Location in appsettings.json " +
                "to a filesystem path (typically a mounted shared/remote location).");
        return Path.GetFullPath(_config.Location);
    }

    /// <summary>
    /// Creates a tar.gz of the project roots directory and writes it to the
    /// configured backup location. Returns metadata about the new archive.
    /// </summary>
    public async Task<BackupCreateResult> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        var location = RequireLocation();
        Directory.CreateDirectory(location);

        if (!Directory.Exists(_projectRootsDir))
            throw new InvalidOperationException(
                $"ProjectRootsDir '{_projectRootsDir}' does not exist; nothing to back up.");

        var timestamp = DateTimeOffset.UtcNow;
        var fileName = $"{_config.ArchivePrefix}{timestamp.UtcDateTime.ToString(TimestampFormat)}.tar.gz";
        var finalPath = Path.Combine(location, fileName);
        var tempPath = finalPath + ".tmp";

        // Refuse to back up the location into itself (would recurse).
        if (IsSubPath(_projectRootsDir, location))
            throw new InvalidOperationException(
                "Backup:Location is inside ProjectRootsDir; choose a path outside the data directory.");

        _logger.LogInformation("Creating backup of {RootsDir} → {Archive}", _projectRootsDir, finalPath);

        try
        {
            await using (var fileStream = File.Create(tempPath))
            await using (var gzip = new GZipStream(fileStream, CompressionLevel.Optimal, leaveOpen: false))
            await using (var tarWriter = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
            {
                // Use manual enumeration: TarFile.CreateFromDirectoryAsync skips dot-prefixed
                // entries (treated as Hidden on Linux), which would silently drop .profiles/,
                // .webhooks/, .godmode-keys/ and every project's .godmode/ chat history.
                await WriteDirectoryToTarAsync(tarWriter, _projectRootsDir, cancellationToken);
            }

            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* swallow */ }
            throw;
        }

        var size = new FileInfo(finalPath).Length;
        _logger.LogInformation("Backup written: {Archive} ({Size:N0} bytes)", finalPath, size);

        if (_config.RetentionCount is int keep && keep > 0)
            PruneOldBackups(location, keep);

        return new BackupCreateResult(fileName, location, size, timestamp);
    }

    /// <summary>Lists archives in the configured location, newest first.</summary>
    public IReadOnlyList<BackupListItem> ListBackups()
    {
        var location = RequireLocation();
        if (!Directory.Exists(location))
            return Array.Empty<BackupListItem>();

        var pattern = $"{_config.ArchivePrefix}*.tar.gz";
        return Directory.EnumerateFiles(location, pattern)
            .Select(path =>
            {
                var info = new FileInfo(path);
                var ts = TryParseTimestamp(info.Name) ?? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                return new BackupListItem(info.Name, info.Length, ts);
            })
            .OrderByDescending(b => b.Timestamp)
            .ToList();
    }

    /// <summary>
    /// Restores the named archive (or the latest one when fileName is null/empty)
    /// over <c>ProjectRootsDir</c>. The previous directory is renamed to
    /// <c>{rootsDir}.before-restore-{timestamp}</c> so the restore is reversible.
    /// </summary>
    public async Task<BackupRestoreResult> RestoreBackupAsync(string? fileName, CancellationToken cancellationToken = default)
    {
        var location = RequireLocation();
        if (!Directory.Exists(location))
            throw new InvalidOperationException($"Backup location '{location}' does not exist.");

        // Resolve archive: explicit filename or newest in location
        string archivePath;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            var latest = ListBackups().FirstOrDefault();
            if (latest == null)
                throw new InvalidOperationException("No backup archives found at the configured location.");
            archivePath = Path.Combine(location, latest.FileName);
            fileName = latest.FileName;
        }
        else
        {
            ValidateFileName(fileName);
            archivePath = Path.Combine(location, fileName);
            if (!File.Exists(archivePath))
                throw new FileNotFoundException($"Backup '{fileName}' not found in '{location}'.", archivePath);
        }

        // Stage extraction in a temp directory adjacent to ProjectRootsDir,
        // then atomically swap so a partial extract can never corrupt live state.
        var parent = Path.GetDirectoryName(_projectRootsDir.TrimEnd(Path.DirectorySeparatorChar))
            ?? throw new InvalidOperationException("Cannot determine parent directory of ProjectRootsDir.");
        Directory.CreateDirectory(parent);

        var stamp = DateTimeOffset.UtcNow.UtcDateTime.ToString(TimestampFormat);
        var stagingDir = _projectRootsDir + ".restore-" + stamp;
        var beforeDir = _projectRootsDir + ".before-restore-" + stamp;

        _logger.LogInformation("Restoring {Archive} → {RootsDir} (staging: {Staging})",
            archivePath, _projectRootsDir, stagingDir);

        Directory.CreateDirectory(stagingDir);
        var fileCount = 0;
        try
        {
            await using (var fileStream = File.OpenRead(archivePath))
            await using (var gzip = new GZipStream(fileStream, CompressionMode.Decompress, leaveOpen: false))
            {
                await TarFile.ExtractToDirectoryAsync(
                    source: gzip,
                    destinationDirectoryName: stagingDir,
                    overwriteFiles: true,
                    cancellationToken: cancellationToken);
            }

            fileCount = Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories).Count();

            // Swap: rename existing → before-restore-*, rename staging → live.
            if (Directory.Exists(_projectRootsDir))
                Directory.Move(_projectRootsDir, beforeDir);
            Directory.Move(stagingDir, _projectRootsDir);
        }
        catch
        {
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); } catch { /* swallow */ }
            throw;
        }

        _logger.LogInformation("Restore complete: {Files} files written to {RootsDir}; previous moved to {Before}",
            fileCount, _projectRootsDir, beforeDir);

        return new BackupRestoreResult(fileName!, _projectRootsDir, beforeDir, fileCount);
    }

    private static DateTimeOffset? TryParseTimestamp(string fileName)
    {
        // Try parsing the trailing TimestampFormat segment of the filename stem.
        const string suffix = ".tar.gz";
        if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        var stem = fileName[..^suffix.Length];
        if (stem.Length < TimestampFormat.Length) return null;
        var candidate = stem[^TimestampFormat.Length..];
        if (DateTime.TryParseExact(candidate, TimestampFormat, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt))
            return new DateTimeOffset(dt, TimeSpan.Zero);
        return null;
    }

    private void PruneOldBackups(string location, int keep)
    {
        var archives = ListBackups();
        var toDelete = archives.Skip(keep).ToList();
        foreach (var item in toDelete)
        {
            try
            {
                File.Delete(Path.Combine(location, item.FileName));
                _logger.LogInformation("Pruned old backup: {File}", item.FileName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to prune backup {File}", item.FileName);
            }
        }
    }

    /// <summary>
    /// Recursively writes <paramref name="rootDir"/> into <paramref name="tar"/>, including
    /// dot-prefixed (hidden) files and directories. Mirrors what
    /// <see cref="TarFile.CreateFromDirectoryAsync(string, Stream, bool, CancellationToken)"/>
    /// does, except without the default Hidden/System attribute filter.
    /// </summary>
    private static async Task WriteDirectoryToTarAsync(TarWriter tar, string rootDir, CancellationToken ct)
    {
        var rootFull = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar);
        var stack = new Stack<string>();
        stack.Push(rootFull);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            // Subdirectories first (push so we recurse depth-first; order is unimportant for tar).
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                // Don't follow symlinked directories — avoids cycles and surprises.
                var info = new DirectoryInfo(sub);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                stack.Push(sub);
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                ct.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                var rel = Path.GetRelativePath(rootFull, file).Replace(Path.DirectorySeparatorChar, '/');
                try
                {
                    await tar.WriteEntryAsync(file, rel, ct);
                }
                catch (IOException) when (!File.Exists(file))
                {
                    // File vanished between enumeration and write (e.g. log rotation). Skip.
                }
            }
        }
    }

    private static void ValidateFileName(string fileName)
    {
        if (fileName.Contains("..") || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || fileName.Contains('/') || fileName.Contains('\\'))
            throw new ArgumentException("Invalid backup filename.", nameof(fileName));
    }

    private static bool IsSubPath(string parent, string candidate)
    {
        var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var c = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }
}
