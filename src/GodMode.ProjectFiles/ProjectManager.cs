namespace GodMode.ProjectFiles;

/// <summary>
/// Named project roots, and the sessions in their working folders.
/// VCS-agnostic — all creation logic lives in scripts, not here.
/// </summary>
public sealed class ProjectManager
{
    private readonly Dictionary<string, string> _projectRoots;

    /// <summary>
    /// Gets the named project roots.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProjectRoots => _projectRoots;

    /// <summary>
    /// Creates a new ProjectManager with the specified named project roots.
    /// </summary>
    /// <param name="projectRoots">Dictionary of named project roots (name -> path). Empty when the server has no roots.</param>
    /// <exception cref="ArgumentException">Thrown when a root's name or path is empty.</exception>
    public ProjectManager(IReadOnlyDictionary<string, string> projectRoots)
    {
        ArgumentNullException.ThrowIfNull(projectRoots);

        _projectRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, path) in projectRoots)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Project root name cannot be empty.", nameof(projectRoots));

            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException($"Project root path for '{name}' cannot be empty.", nameof(projectRoots));

            var fullPath = Path.GetFullPath(path);

            // Ensure root directory exists
            if (!Directory.Exists(fullPath))
                Directory.CreateDirectory(fullPath);

            _projectRoots[name] = fullPath;
        }
    }

    /// <summary>
    /// Gets the path for a named project root.
    /// </summary>
    /// <param name="rootName">The name of the project root.</param>
    /// <returns>The full path to the project root.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when root name is not found.</exception>
    public string GetProjectRootPath(string rootName)
    {
        if (!_projectRoots.TryGetValue(rootName, out var path))
            throw new KeyNotFoundException($"Project root '{rootName}' not found.");

        return path;
    }

    /// <summary>
    /// Gets all root paths as (name, path) pairs.
    /// </summary>
    public IEnumerable<(string Name, string Path)> GetAllRootPaths()
        => _projectRoots.Select(kvp => (kvp.Key, kvp.Value));

    /// <summary>
    /// The sessions in a root: in each of its working folders (its immediate subfolders but its own,
    /// <see cref="ProjectFolder.ReservedFolderNames"/>), each of <see cref="SessionState.List"/>.
    /// </summary>
    /// <param name="rootName">The name of the project root.</param>
    public IReadOnlyList<(string WorkingFolder, string SessionId)> ListSessions(string rootName)
    {
        return WorkingFolders(rootName).SelectMany(folder => SessionState.List(folder).Select(id => (folder, id))).ToArray();
    }

    /// <summary>The trashed sessions of a root: in each of its working folders, each of <see cref="SessionState.ListTrashed"/>.</summary>
    public IReadOnlyList<(string WorkingFolder, string SessionId)> ListTrashed(string rootName) =>
        WorkingFolders(rootName).SelectMany(folder => SessionState.ListTrashed(folder).Select(id => (folder, id))).ToArray();

    /// <summary>
    /// The root's working folders: its immediate subfolders, in ordinal order, but its own. A folder the
    /// root keeps for itself is never a working folder, even one with sessions in it from before such
    /// names were refused: a delete would delete the root's config or its logs.
    /// </summary>
    private IEnumerable<string> WorkingFolders(string rootName)
    {
        var rootPath = GetProjectRootPath(rootName);
        return Directory.Exists(rootPath)
            ? Directory.GetDirectories(rootPath)
                .Where(folder => !ProjectFolder.IsReservedFolderName(Path.GetFileName(folder)))
                .Order(StringComparer.Ordinal)
            : [];
    }

    /// <summary>
    /// Whether a working folder of the root has a session with <paramref name="sessionId"/>, on disk
    /// (with a status.json or not), or in its trash: an id is unique within its root, and a trashed
    /// session keeps its id for an undo until the trash is purged.
    /// </summary>
    public bool HasSession(string rootName, string sessionId)
    {
        var rootPath = GetProjectRootPath(rootName);
        return Directory.Exists(rootPath)
            && Directory.GetDirectories(rootPath).Any(folder => Directory.Exists(SessionState.PathOf(folder, sessionId))
                || Directory.Exists(SessionState.TrashedPathOf(folder, sessionId)));
    }

    /// <summary>
    /// Converts a display name to a path-safe project folder name.
    /// Spaces become underscores; invalid filename characters are removed, and so are trailing dots,
    /// which Windows drops from a folder name (<c>foo.</c> is the folder <c>foo</c>).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The name leaves no folder of its own: empty once cleaned, or dots only (<c>.</c>, <c>..</c>).
    /// </exception>
    public static string ConvertNameToPath(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => c == ' ' ? '_' : c)
            .Where(c => !invalidChars.Contains(c) && c is not ('/' or '\\'))
            .ToArray());
        // Dots only ("..") is left as it is, to be refused as no folder of its own
        if (cleaned.Any(c => c != '.'))
            cleaned = cleaned.TrimEnd('.');
        ProjectFolder.ValidateFolderName(cleaned, nameof(name));
        return cleaned;
    }
}
