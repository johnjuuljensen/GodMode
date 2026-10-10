using GodMode.Server.Auth;
using GodMode.Server.Services;

namespace GodMode.Server;

/// <summary>
/// Where the server's own files may not be: its API key file (<see cref="ApiKeyFile"/>) and its instance's
/// config file (<see cref="InstanceConfig"/>), which can hold the key and the profiles' secrets, are never
/// in a folder sessions work in: a scan folder, an explicit root, or the fallback root.
/// </summary>
public static class ServerFiles
{
    /// <summary>The setting the fallback root is named by in what the server says: it has none of its own.</summary>
    public const string FallbackRootSetting = "the fallback root";

    /// <summary>Every folder sessions may work in that <paramref name="config"/> names: its root sources, then the fallback root.</summary>
    public static IEnumerable<(string Setting, string Folder)> SessionFolders(IConfiguration config) =>
        RootSources.From(config).Folders.Append((FallbackRootSetting, ServerDataDirectory.FallbackRoot(config)));

    /// <summary>The first of <see cref="SessionFolders"/> whose tree holds <paramref name="path"/>; null when none does.</summary>
    public static (string Setting, string Folder)? FolderHolding(string path, IConfiguration config) =>
        SessionFolders(config).Where(folder => IsUnder(folder.Folder, path)).Select(folder => ((string, string)?)folder).FirstOrDefault();

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or in its tree.</summary>
    public static bool IsUnder(string folder, string path) =>
        Path.GetRelativePath(folder, path) is var relative
        && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(relative);
}
