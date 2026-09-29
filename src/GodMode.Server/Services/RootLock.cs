using System.Text.Json;
using GodMode.Shared;
using ProjectFiles = GodMode.ProjectFiles;

namespace GodMode.Server.Services;

/// <summary>
/// A server's hold on one root, so no two servers manage its sessions: <c>{root}/logs/server.lock</c>,
/// kept open exclusively (<see cref="FileShare.None"/>) for as long as the server manages the root. The
/// OS releases it on any exit, a crash or a kill included, so it never goes stale as a PID file would.
/// Beside it, <c>server.json</c> names the holder (its instance and process) for the servers it keeps out.
/// Both are in <c>{root}/logs/</c>, the server's own folder, whose <c>.gitignore</c> keeps it out of git
/// when the root's folder is under source control.
/// </summary>
internal sealed class RootLock : IDisposable
{
    public const string LockFileName = "server.lock";
    public const string HolderFileName = "server.json";

    private readonly FileStream _lock;

    private RootLock(FileStream lockFile) => _lock = lockFile;

    /// <summary>Who holds a root: the <c>Instance</c> of the server, and its process.</summary>
    public sealed record Holder(string Instance, int ProcessId);

    /// <summary>
    /// Takes the lock on the root at <paramref name="rootPath"/> for <paramref name="instance"/>, or
    /// returns null while another server (or another holder in this process) has it.
    /// </summary>
    public static RootLock? TryAcquire(string rootPath, string instance)
    {
        var logsDir = Path.Combine(rootPath, ProjectFiles.ProjectFolder.ScriptLogsFolderName);
        ProjectFiles.ProjectFolder.EnsureIgnoredByGit(logsDir);
        FileStream lockFile;
        try
        {
            lockFile = new FileStream(Path.Combine(logsDir, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }

        try
        {
            File.WriteAllText(Path.Combine(logsDir, HolderFileName),
                JsonSerializer.Serialize(new Holder(instance, Environment.ProcessId), JsonDefaults.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The lock holds without it: the servers kept out only cannot name who holds the root
        }
        return new RootLock(lockFile);
    }

    /// <summary>Who holds the root at <paramref name="rootPath"/>, as its holder wrote it; null when that cannot be read.</summary>
    public static Holder? ReadHolder(string rootPath)
    {
        try
        {
            var path = Path.Combine(rootPath, ProjectFiles.ProjectFolder.ScriptLogsFolderName, HolderFileName);
            return JsonSerializer.Deserialize<Holder>(File.ReadAllText(path), JsonDefaults.Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _lock.Dispose();
}
