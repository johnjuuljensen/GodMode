namespace GodMode.Server.Services;

/// <summary>
/// The MCP config a Claude process is launched with (<c>--mcp-config</c> takes a file path):
/// GodMode's own server, and the fleet's for a session with the fleet's tools. It lives beside the session's record in
/// the root's <c>logs</c> (<see cref="PathFor"/>), out of its working folder: its token speaks for the session
/// (<c>message_parent</c>, and the fleet's tools when it has them), and a shared working folder is its neighbours' too.
/// Owner-only where the OS supports it, and only for as long as the process runs.
/// </summary>
public static class McpConfigFile
{
    public const string FileName = "mcp-config.json";

    /// <summary>Where a session has its config: <c>{root}/logs/{id}.mcp-config.json</c>, out of its working folder.</summary>
    public static string PathFor(string rootPath, string sessionId) =>
        Path.Combine(rootPath, GodMode.ProjectFiles.ProjectFolder.ScriptLogsFolderName, $"{sessionId}.{FileName}");

    /// <summary>Writes the config to <paramref name="path"/> and returns it.</summary>
    public static string WriteFile(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // The create mode applies only to a new file, so replace rather than overwrite
        File.Delete(path);
        if (OperatingSystem.IsWindows())
        {
            // Inherits the project folder's ACL
            File.WriteAllText(path, json);
            return path;
        }

        try
        {
            using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            using var writer = new StreamWriter(stream);
            writer.Write(json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // A file system without Unix permissions (network mounts): write it plainly
            File.WriteAllText(path, json);
        }
        return path;
    }

    /// <summary>
    /// Deletes the config at <paramref name="path"/> if it was written before <paramref name="launchedAtUtc"/>. A later
    /// file belongs to a newer launch of the same project and is left alone.
    /// </summary>
    public static void DeleteFileIfWrittenBefore(string path, DateTime launchedAtUtc)
    {
        if (File.Exists(path) && File.GetLastWriteTimeUtc(path) <= launchedAtUtc)
            File.Delete(path);
    }
}
