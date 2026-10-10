using GodMode.Server.Auth;

namespace GodMode.Server;

/// <summary>
/// The server's own data directory, where it keeps what is its and not a root's: the generated API key
/// (<see cref="ApiKeyFile"/>), and the root of a server with none configured (<see cref="FallbackRoot"/>).
/// It is the folder of <c>Authentication:ApiKeyFile</c> when that is set, else <see cref="Default"/>; it
/// does not depend on the working directory the server was started in.
/// </summary>
public static class ServerDataDirectory
{
    public const string DirectoryName = "GodMode.Server";

    /// <summary>The folder in it of the root a server with no roots has: <c>{data directory}/projects</c>.</summary>
    public const string FallbackRootName = "projects";

    /// <summary>
    /// The user's local application data: <c>%LOCALAPPDATA%\GodMode.Server</c> on Windows,
    /// <c>$XDG_DATA_HOME/GodMode.Server</c> (by default <c>~/.local/share</c>) on Linux. Null when the OS
    /// names none (no home directory).
    /// </summary>
    public static string? Default() =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify) is { Length: > 0 } dir
            ? Path.Combine(dir, DirectoryName)
            : null;

    /// <summary>The data directory's full path: the key file's folder, else <see cref="Default"/>; null when there is neither.</summary>
    public static string? From(IConfiguration config) =>
        (config[ApiKeyFile.PathSetting] is { Length: > 0 } keyFile ? Path.GetDirectoryName(Path.GetFullPath(keyFile)) : Default())
            is { } dir ? Path.GetFullPath(dir) : null;

    /// <summary>
    /// The full path of the root a server with no roots has (the <c>Default</c> profile's <c>default</c>
    /// root): <c>projects</c> in the data directory, or in the working directory when there is no data
    /// directory (no home directory and no key file configured).
    /// </summary>
    public static string FallbackRoot(IConfiguration config) =>
        Path.GetFullPath(Path.Combine(From(config) ?? Directory.GetCurrentDirectory(), FallbackRootName));
}
