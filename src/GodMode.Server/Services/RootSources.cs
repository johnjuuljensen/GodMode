namespace GodMode.Server.Services;

/// <summary>
/// Where a server's roots come from, and what its profiles carry, as its config names them: keyed maps,
/// so that appsettings, the instance's config file, environment variables and the command line merge
/// entry by entry (.NET merges arrays by index). Read fresh from the configuration on every rebuild.
/// <list type="bullet">
/// <item><c>Roots:Scan:&lt;key&gt; = &lt;folder&gt;</c>: each immediate subfolder with <c>.godmode-root/</c> is a root, named after it.</item>
/// <item><c>Roots:Explicit:&lt;name&gt;:Path = &lt;folder&gt;</c>, optionally <c>:Profile</c> and <c>:Title</c>: the folder is the root <c>&lt;name&gt;</c>.</item>
/// <item><c>Profiles:&lt;name&gt;:Description</c>, and <c>Profiles:&lt;name&gt;:Environment:&lt;VAR&gt; = &lt;value&gt;</c>.</item>
/// </list>
/// An entry with an empty folder is none, so a later source can turn off one an earlier source set.
/// Relative folders resolve against the working directory.
/// </summary>
public sealed record RootSources(
    IReadOnlyList<ScanFolder> ScanFolders,
    IReadOnlyList<ExplicitRoot> ExplicitRoots,
    IReadOnlyDictionary<string, ProfileSettings> Profiles)
{
    public const string ScanSection = "Roots:Scan";
    public const string ExplicitSection = "Roots:Explicit";
    public const string ProfilesSection = "Profiles";

    /// <summary>The server's root sources in <paramref name="config"/>: scan folders and explicit roots in ordinal order of their keys.</summary>
    public static RootSources From(IConfiguration config) => new(
        config.GetSection(ScanSection).GetChildren()
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new ScanFolder(entry.Key, FullPath(entry.Value!)))
            .ToArray(),
        config.GetSection(ExplicitSection).GetChildren()
            .Where(entry => !string.IsNullOrWhiteSpace(entry["Path"]))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new ExplicitRoot(entry.Key, FullPath(entry["Path"]!),
                entry["Profile"] is { Length: > 0 } profile ? profile : null,
                entry["Title"] is { Length: > 0 } title ? title : null))
            .ToArray(),
        config.GetSection(ProfilesSection).GetChildren().ToDictionary(
            profile => profile.Key,
            profile => new ProfileSettings(
                profile["Description"] is { Length: > 0 } description ? description : null,
                profile.GetSection("Environment").GetChildren()
                    .Where(variable => variable.Value != null)
                    .ToDictionary(variable => variable.Key, variable => variable.Value!)),
            StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Settings that once named roots and are not read any more, each with what takes its place:
    /// <c>ProjectRootsDir</c>, and a profile's <c>Roots</c> (<c>Profiles:&lt;p&gt;:Roots:&lt;name&gt;</c>).
    /// </summary>
    public static IEnumerable<string> RetiredSettings(IConfiguration config)
    {
        if (config["ProjectRootsDir"] is { Length: > 0 } folder)
            yield return $"ProjectRootsDir ({folder}) is not read: name the folder as a scan folder, {ScanSection}:<key>";
        foreach (var profile in config.GetSection(ProfilesSection).GetChildren())
            foreach (var root in profile.GetSection("Roots").GetChildren())
                yield return $"{ProfilesSection}:{profile.Key}:Roots:{root.Key} ({root.Value}) is not read: " +
                    $"name it as an explicit root, {ExplicitSection}:{root.Key}:Path, with :Profile {profile.Key}";
    }

    /// <summary>Every folder the roots come from, where sessions work: the scan folders and the explicit roots.</summary>
    public IEnumerable<(string Setting, string Folder)> Folders =>
        ScanFolders.Select(scan => ($"{ScanSection}:{scan.Key}", scan.Folder))
            .Concat(ExplicitRoots.Select(root => ($"{ExplicitSection}:{root.Name}:Path", root.Path)));

    private static string FullPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

/// <summary>A folder whose immediate subfolders with <c>.godmode-root/</c> are roots: <c>Roots:Scan:&lt;Key&gt;</c>.</summary>
public sealed record ScanFolder(string Key, string Folder);

/// <summary>
/// A root named in config, anywhere on disk: <c>Roots:Explicit:&lt;Name&gt;</c>. <paramref name="Profile"/>
/// is its profile, and <paramref name="Title"/> its title, when the root's own config.json names none.
/// </summary>
public sealed record ExplicitRoot(string Name, string Path, string? Profile, string? Title = null);

/// <summary>A profile's settings: <c>Profiles:&lt;name&gt;</c>. Its environment reaches its sessions and scripts, under the root's own.</summary>
public sealed record ProfileSettings(string? Description, IReadOnlyDictionary<string, string> Environment);
