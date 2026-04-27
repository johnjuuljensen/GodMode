namespace GodMode.Server.Models;

/// <summary>
/// Configuration for the backup/restore feature.
/// Bound from the "Backup" section of appsettings.json.
/// </summary>
public sealed class BackupConfig
{
    /// <summary>
    /// Filesystem path where backup archives are written and read.
    /// Typically a mounted shared/remote location (Azure Files, NFS, EFS,
    /// S3-via-FUSE, etc.). Required for backup/restore to work.
    /// </summary>
    public string? Location { get; set; }

    /// <summary>
    /// Optional. When set, after a successful backup the service prunes
    /// older archives (matching <see cref="ArchivePrefix"/>*.tar.gz) so that
    /// only the most recent <c>RetentionCount</c> archives remain.
    /// </summary>
    public int? RetentionCount { get; set; }

    /// <summary>
    /// Filename prefix used for archives. Defaults to "godmode-backup-".
    /// Final filename pattern: {ArchivePrefix}{yyyy-MM-dd_HHmmss}.tar.gz
    /// </summary>
    public string ArchivePrefix { get; set; } = "godmode-backup-";
}

public sealed record BackupListItem(string FileName, long SizeBytes, DateTimeOffset Timestamp);

public sealed record BackupCreateResult(string FileName, string Location, long SizeBytes, DateTimeOffset Timestamp);

public sealed record BackupRestoreRequest(string? FileName);

public sealed record BackupRestoreResult(string FileName, string RestoredTo, string PreviousMovedTo, int FilesRestored);
