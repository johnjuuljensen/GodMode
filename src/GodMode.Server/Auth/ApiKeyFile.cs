using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace GodMode.Server.Auth;

/// <summary>
/// The API key of a server with none configured: 256 random bits, generated on its first start into a
/// file only the server's user can read, and read from there on every later start. The file lives in
/// the server's own data directory (<see cref="DefaultPath"/>, or <c>Authentication:ApiKeyFile</c>),
/// never under <c>ProjectRootsDir</c>, where sessions work.
/// </summary>
public static class ApiKeyFile
{
    public const string PathSetting = "Authentication:ApiKeyFile";
    public const string FileName = "api-key";
    public const string DirectoryName = "GodMode.Server";

    /// <summary>
    /// The user's local application data: <c>%LOCALAPPDATA%\GodMode.Server\api-key</c> on Windows,
    /// <c>$XDG_DATA_HOME/GodMode.Server/api-key</c> (by default <c>~/.local/share</c>) on Linux. Null
    /// when the OS names none (no home directory).
    /// </summary>
    public static string? DefaultPath() =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify) is { Length: > 0 } dir
            ? Path.Combine(dir, DirectoryName, FileName)
            : null;

    /// <summary>The key file's full path: <c>Authentication:ApiKeyFile</c>, else <see cref="DefaultPath"/>, and not under <c>ProjectRootsDir</c>.</summary>
    public static string PathFrom(IConfiguration config)
    {
        var path = (config[PathSetting] is { Length: > 0 } configured ? configured : DefaultPath())
            ?? throw new StartupConfigurationException(
                $"GodMode.Server will not start: no API key is configured, and this user has no local application data " +
                $"directory to keep a generated one in. Set {AuthModeSelector.ApiKeySetting}, or {PathSetting} to a file this user can write.");
        path = Path.GetFullPath(path);

        var rootsDir = Path.GetFullPath(config["ProjectRootsDir"] is { Length: > 0 } dir ? dir : "roots");
        var relative = Path.GetRelativePath(rootsDir, path);
        if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(relative))
            throw new StartupConfigurationException(
                $"GodMode.Server will not start: its API key file, {path}, would be under ProjectRootsDir ({rootsDir}), " +
                $"where sessions work. Set {PathSetting} to a file outside it, or set {AuthModeSelector.ApiKeySetting}.");
        return path;
    }

    /// <summary>The key in <paramref name="path"/>, or a new one written there (owner-only) when it has none.</summary>
    public static (string Key, bool Created) LoadOrCreate(string path)
    {
        try
        {
            if (Read(path) is { } existing) return (existing, false);

            var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            // A file without a key, from a first start that stopped halfway, is replaced
            if (File.Exists(path)) File.Delete(path);
            try
            {
                Write(path, key);
            }
            catch (IOException) when (Read(path) is { } other)
            {
                // Another start of this server wrote one first
                return (other, false);
            }
            return (key, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StartupConfigurationException(
                $"GodMode.Server will not start: it cannot read or create its API key file, {path} ({ex.Message}). " +
                $"Set {PathSetting} to a file this user can write, or set {AuthModeSelector.ApiKeySetting}.", ex);
        }
    }

    private static string? Read(string path)
    {
        try { return File.ReadAllText(path).Trim() is { Length: > 0 } key ? key : null; }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void Write(string path, string key)
    {
        var dir = Path.GetDirectoryName(path)!;
        FileStream stream;
        try
        {
            stream = OperatingSystem.IsWindows() ? CreateOwnerOnlyOnWindows(dir, path) : CreateOwnerOnlyOnUnix(dir, path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            // A file system that keeps no owner-only permissions (a network mount): a plain file, as every file there is
            Directory.CreateDirectory(dir);
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        }
        using (stream)
        using (var writer = new StreamWriter(stream))
            writer.Write(key);
    }

    /// <summary>
    /// Created with its mode (0700 directory, 0600 file) rather than changed afterwards, so nothing
    /// calls chmod, which some file systems (Azure Files, other network mounts) refuse. A directory
    /// that already exists keeps its mode.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private static FileStream CreateOwnerOnlyOnUnix(string dir, string path)
    {
        Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }

    /// <summary>Created with an ACL of the server's user alone, inheriting nothing from the directory.</summary>
    [SupportedOSPlatform("windows")]
    private static FileStream CreateOwnerOnlyOnWindows(string dir, string path)
    {
        Directory.CreateDirectory(dir);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.Read,
            FileShare.None, bufferSize: 4096, FileOptions.None, security);
    }
}
