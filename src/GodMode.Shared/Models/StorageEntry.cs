namespace GodMode.Shared.Models;

/// <summary>
/// A file or directory entry in the storage browser.
/// </summary>
public record StorageEntry(
    string Name,
    string Path,
    bool IsDirectory,
    long Size,
    DateTime ModifiedAt);
