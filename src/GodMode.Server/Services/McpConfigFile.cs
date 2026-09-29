namespace GodMode.Server.Services;

/// <summary>
/// The MCP config a Claude process is launched with (<c>--mcp-config</c> takes a file path):
/// GodMode's own server only. It lives in the session's state folder (<c>.godmode/sessions/{id}/</c>), owner-only where
/// the OS supports it, and only for as long as the process runs.
/// </summary>
public static class McpConfigFile
{
    public const string FileName = "mcp-config.json";

    public static string PathFor(string statePath) => Path.Combine(statePath, FileName);

    /// <summary>Writes the config and returns its path.</summary>
    public static string Write(string statePath, string json)
    {
        var path = PathFor(statePath);
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
    /// Deletes the config if it was written before <paramref name="launchedAtUtc"/>. A later file
    /// belongs to a newer launch of the same project and is left alone.
    /// </summary>
    public static void DeleteIfWrittenBefore(string statePath, DateTime launchedAtUtc)
    {
        var path = PathFor(statePath);
        if (File.Exists(path) && File.GetLastWriteTimeUtc(path) <= launchedAtUtc)
            File.Delete(path);
    }
}
