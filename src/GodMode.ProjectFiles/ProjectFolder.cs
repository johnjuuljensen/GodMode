using System.Text;
using System.Text.Json;
using GodMode.Shared.Models;


namespace GodMode.ProjectFiles;

/// <summary>
/// A working folder, and one session's state in it (<c>.godmode/sessions/{id}/</c>, see <see cref="SessionState"/>).
/// </summary>
public sealed class ProjectFolder : IDisposable
{
    /// <summary>A working folder's GodMode state: <c>{working folder}/.godmode/</c>, all of it out of git.</summary>
    public const string GodModeDirectoryName = ".godmode";
    private const string StatusFileName = "status.json";
    private const string InputFileName = "input.jsonl";
    private const string OutputFileName = "output.jsonl";
    private const string MetricsFileName = "metrics.html";
    private const string GitIgnoreFileName = ".gitignore";
    private const string IgnoreEverything = "*";
    private const string GitIgnoreContent = $"# Exclude all GodMode state files\n{IgnoreEverything}\n";

    /// <summary>A root's own config and scripts: <c>{root}/.godmode-root/</c>.</summary>
    public const string RootConfigFolderName = ".godmode-root";

    /// <summary>A root's script logs and result files: <c>{root}/logs/</c>.</summary>
    public const string ScriptLogsFolderName = "logs";

    /// <summary>Where archived projects went before archiving was removed; a leftover can still be on disk.</summary>
    public const string ArchivedFolderName = ".archived";

    /// <summary>
    /// The folders a root keeps for itself at its top level, which no project may be: a delete of
    /// the project would delete the root's config, every script log, or the state of the sessions
    /// that work in the root itself (<c>{root}/.godmode/</c>, when the root is its own workspace).
    /// Compared ignoring case, and trailing dots and spaces, as Windows compares folder names
    /// (refusing <c>LOGS</c> on Linux too).
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedFolderNames =
        new HashSet<string>([RootConfigFolderName, ScriptLogsFolderName, ArchivedFolderName, GodModeDirectoryName], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The names Windows keeps for devices: a folder of one (with any extension, <c>nul.txt</c>) is
    /// the device, not a folder. Refused on every OS, as the root's own folders are, so a root moves
    /// between hosts with its projects. Compared ignoring case.
    /// </summary>
    private static readonly IReadOnlySet<string> WindowsDeviceNames = new HashSet<string>(
        ["CON", "PRN", "AUX", "NUL",
            .. Enumerable.Range(0, 10).SelectMany(n => new[] { $"COM{n}", $"LPT{n}" }),
            // With a superscript digit too
            "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="folderName"/> is one of the <see cref="ReservedFolderNames"/>, compared
    /// as Windows compares folder names: ignoring case, and trailing dots and spaces.
    /// </summary>
    public static bool IsReservedFolderName(string folderName) =>
        ReservedFolderNames.Contains(folderName.TrimEnd('.', ' '));

    private readonly string _projectPath;
    private readonly string _sessionId;
    private readonly JsonlWriter _inputWriter;
    private readonly JsonlWriter _outputWriter;
    private readonly SemaphoreSlim _statusLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Gets the full path to the working folder, Claude's working directory.
    /// </summary>
    public string ProjectPath => _projectPath;

    /// <summary>
    /// Gets the session's id (<see cref="SessionState"/>). The server's opaque ID is <c>{profile}/{root}/{id}</c>.
    /// </summary>
    public string SessionId => _sessionId;

    /// <summary>
    /// Gets the path to the session's state folder, <c>.godmode/sessions/{id}/</c>, where all its state files are.
    /// </summary>
    public string StatePath => SessionState.PathOf(_projectPath, _sessionId);

    /// <summary>
    /// Gets the path to the status.json file.
    /// </summary>
    public string StatusFilePath => Path.Combine(StatePath, StatusFileName);

    /// <summary>
    /// Gets the path to the input.jsonl file.
    /// </summary>
    public string InputFilePath => Path.Combine(StatePath, InputFileName);

    /// <summary>
    /// Gets the path to the output.jsonl file.
    /// </summary>
    public string OutputFilePath => Path.Combine(StatePath, OutputFileName);

    /// <summary>
    /// Gets the path to the metrics.html file.
    /// </summary>
    public string MetricsFilePath => Path.Combine(StatePath, MetricsFileName);

    private ProjectFolder(string projectPath, string sessionId)
    {
        _projectPath = projectPath;
        _sessionId = sessionId;
        _inputWriter = new JsonlWriter(InputFilePath);
        _outputWriter = new JsonlWriter(OutputFilePath);
    }

    /// <summary>
    /// Creates a new working folder in the root, with nothing in it: its session's state is made
    /// once its id is known (<see cref="SessionState.Create"/>).
    /// </summary>
    /// <returns>The folder's full path.</returns>
    /// <exception cref="ArgumentException">The folder name is not a folder of its own (<see cref="ValidateFolderName"/>).</exception>
    /// <exception cref="IOException">The folder exists.</exception>
    public static string Create(string rootPath, string folderName)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Root path cannot be empty.", nameof(rootPath));

        ValidateFolderName(folderName, nameof(folderName));

        var projectPath = Path.Combine(rootPath, folderName);
        if (Directory.Exists(projectPath))
            throw new IOException($"FOLDER_EXISTS:{projectPath}");

        Directory.CreateDirectory(projectPath);
        return projectPath;
    }

    /// <summary>
    /// A working folder that sessions share (<c>sharedFolder</c>): made when it is missing, used as it
    /// is when it is there, so two creates into one new shared folder at once both have it.
    /// </summary>
    /// <returns>The folder's full path, and whether this call made it.</returns>
    public static (string Path, bool Made) CreateShared(string rootPath, string folderName)
    {
        ValidateFolderName(folderName, nameof(folderName));
        var projectPath = Path.Combine(rootPath, folderName);
        var made = !Directory.Exists(projectPath);
        Directory.CreateDirectory(projectPath);
        return (projectPath, made);
    }

    /// <summary>
    /// An existing working folder in the root, reused as it is: its files stay, and the new session's
    /// state is made in its <c>.godmode/sessions/</c>.
    /// </summary>
    /// <returns>The folder's full path.</returns>
    public static string Reuse(string rootPath, string folderName)
    {
        ValidateFolderName(folderName, nameof(folderName));
        var projectPath = Path.Combine(rootPath, folderName);
        if (!Directory.Exists(projectPath))
            throw new DirectoryNotFoundException($"Project folder not found: {projectPath}");
        return projectPath;
    }

    /// <summary>
    /// Throws unless <paramref name="folderName"/> names a folder of its own inside the root: not
    /// empty, no path separators or other invalid characters, and not made of dots and spaces only.
    /// <c>.</c> and <c>..</c> are the root and its parent, and Windows strips trailing dots and
    /// spaces, so <c>...</c> is the root too; a delete of such a project deletes that recursively.
    /// Nor may it be one of the <see cref="ReservedFolderNames"/>. Nor may it end in a dot or a space,
    /// which Windows drops: <c>foo.</c> would be the folder <c>foo</c>, another project's, and its ID
    /// would change at the next recovery. Nor a Windows device name (<c>CON</c>, <c>NUL</c>,
    /// <c>COM1</c>), which is no folder there. Both are refused on every OS.
    /// </summary>
    public static void ValidateFolderName(string? folderName, string paramName = "folderName")
    {
        if (string.IsNullOrWhiteSpace(folderName))
            throw new ArgumentException("Project folder name cannot be empty.", paramName);

        if (folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || folderName.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException($"Project folder name '{folderName}' contains invalid characters.", paramName);

        if (folderName.All(c => c is '.' or ' '))
            throw new ArgumentException($"'{folderName}' is not a valid project folder name.", paramName);

        if (IsReservedFolderName(folderName))
            throw new ArgumentException($"'{folderName}' is a folder the project root uses for itself.", paramName);

        if (folderName[^1] is '.' or ' ')
            throw new ArgumentException($"Project folder name '{folderName}' ends in a dot or a space, which Windows drops from a folder name.", paramName);

        if (WindowsDeviceNames.Contains(folderName.Split('.')[0].TrimEnd(' ')))
            throw new ArgumentException($"'{folderName}' is a Windows device name, not a folder name.", paramName);
    }

    /// <summary>
    /// Makes sure <c>.godmode/.gitignore</c> keeps everything in <c>.godmode</c> out of git (its MCP
    /// config holds the project token while claude runs): written if missing, and the rule appended,
    /// keeping its lines, to one that lacks it (a checkout's own). Creates <c>.godmode</c> if need be.
    /// </summary>
    public static void EnsureGitIgnore(string projectPath) => EnsureIgnoredByGit(Path.Combine(projectPath, GodModeDirectoryName));

    /// <summary>
    /// Keeps <paramref name="folder"/> and everything in it out of git: its <c>.gitignore</c> ignores
    /// everything, written if missing and the rule appended to one that lacks it. Creates the folder if need be.
    /// </summary>
    public static void EnsureIgnoredByGit(string folder)
    {
        // One check and write at a time: two first creates in one shared folder would both find no
        // .gitignore and write it at once, and on Windows the second write fails
        lock (GitIgnoreLock)
        {
            Directory.CreateDirectory(folder);
            var gitIgnorePath = Path.Combine(folder, GitIgnoreFileName);
            if (!File.Exists(gitIgnorePath))
            {
                File.WriteAllText(gitIgnorePath, GitIgnoreContent, Encoding.UTF8);
                return;
            }

            var existing = File.ReadAllText(gitIgnorePath);
            if (existing.Split('\n').Any(line => line.Trim() == IgnoreEverything)) return;
            var separator = existing.Length == 0 || existing.EndsWith('\n') ? "" : "\n";
            File.AppendAllText(gitIgnorePath, separator + GitIgnoreContent);
        }
    }

    private static readonly Lock GitIgnoreLock = new();

    /// <summary>
    /// Opens a session's state in an existing working folder.
    /// </summary>
    /// <param name="projectPath">Path to the working folder.</param>
    /// <param name="sessionId">The session's id, its folder in <c>.godmode/sessions/</c>.</param>
    /// <returns>A ProjectFolder instance.</returns>
    /// <exception cref="DirectoryNotFoundException">Thrown when the working folder doesn't exist.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the session has no status.json.</exception>
    public static ProjectFolder Open(string projectPath, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            throw new ArgumentException("Project path cannot be empty.", nameof(projectPath));

        if (!SessionState.IsId(sessionId))
            throw new ArgumentException($"'{sessionId}' is not a session id.", nameof(sessionId));

        if (!Directory.Exists(projectPath))
            throw new DirectoryNotFoundException($"Project folder not found: {projectPath}");

        var statusPath = Path.Combine(SessionState.PathOf(projectPath, sessionId), StatusFileName);
        if (!File.Exists(statusPath))
            throw new FileNotFoundException($"Status file not found: {statusPath}");

        return new ProjectFolder(projectPath, sessionId);
    }

    /// <summary>
    /// Reads and deserializes the status.json file.
    /// </summary>
    /// <returns>The current project status.</returns>
    /// <exception cref="FileNotFoundException">Thrown when status.json doesn't exist.</exception>
    /// <exception cref="JsonException">Thrown when JSON parsing fails.</exception>
    public async Task<ProjectStatus> ReadStatusAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(StatusFilePath))
            throw new FileNotFoundException($"Status file not found: {StatusFilePath}");

        await _statusLock.WaitAsync(cancellationToken);
        try
        {
            var json = await File.ReadAllTextAsync(StatusFilePath, Encoding.UTF8, cancellationToken);
            return JsonSerializer.Deserialize<ProjectStatus>(json, ProjectJsonContext.Default.ProjectStatus)
                ?? throw new JsonException("Failed to deserialize status.json");
        }
        finally
        {
            _statusLock.Release();
        }
    }

    /// <summary>
    /// Reads and deserializes the status.json file synchronously.
    /// </summary>
    /// <returns>The current project status.</returns>
    /// <exception cref="FileNotFoundException">Thrown when status.json doesn't exist.</exception>
    /// <exception cref="JsonException">Thrown when JSON parsing fails.</exception>
    public ProjectStatus ReadStatus()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(StatusFilePath))
            throw new FileNotFoundException($"Status file not found: {StatusFilePath}");

        _statusLock.Wait();
        try
        {
            var json = File.ReadAllText(StatusFilePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<ProjectStatus>(json, ProjectJsonContext.Default.ProjectStatus)
                ?? throw new JsonException("Failed to deserialize status.json");
        }
        finally
        {
            _statusLock.Release();
        }
    }

    /// <summary>
    /// Serializes and writes the status.json file.
    /// </summary>
    /// <param name="status">The status to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="IOException">Thrown when file I/O fails.</exception>
    public async Task WriteStatusAsync(ProjectStatus status, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(status);

        await _statusLock.WaitAsync(cancellationToken);
        try
        {
            var json = JsonSerializer.Serialize(status, ProjectJsonContext.Default.ProjectStatus);
            await AtomicFile.WriteAllTextAsync(StatusFilePath, json, Encoding.UTF8, cancellationToken);
        }
        finally
        {
            _statusLock.Release();
        }
    }

    /// <summary>
    /// Serializes and writes the status.json file synchronously.
    /// </summary>
    /// <param name="status">The status to write.</param>
    /// <exception cref="IOException">Thrown when file I/O fails.</exception>
    public void WriteStatus(ProjectStatus status)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(status);

        _statusLock.Wait();
        try
        {
            var json = JsonSerializer.Serialize(status, ProjectJsonContext.Default.ProjectStatus);
            AtomicFile.WriteAllText(StatusFilePath, json, Encoding.UTF8);
        }
        finally
        {
            _statusLock.Release();
        }
    }

    /// <summary>
    /// Updates the status.json file using a transformation function.
    /// </summary>
    /// <param name="updateFunc">Function to transform the current status.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated status.</returns>
    public async Task<ProjectStatus> UpdateStatusAsync(
        Func<ProjectStatus, ProjectStatus> updateFunc,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(updateFunc);

        await _statusLock.WaitAsync(cancellationToken);
        try
        {
            var currentStatus = await ReadStatusAsync(cancellationToken);
            var updatedStatus = updateFunc(currentStatus);
            await WriteStatusAsync(updatedStatus, cancellationToken);
            return updatedStatus;
        }
        finally
        {
            _statusLock.Release();
        }
    }

    /// <summary>
    /// Appends an event to the input.jsonl file.
    /// </summary>
    /// <param name="evt">The event to append.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task AppendInputAsync(OutputEvent evt, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _inputWriter.AppendAsync(evt, cancellationToken);
    }

    /// <summary>
    /// Appends an event to the input.jsonl file synchronously.
    /// </summary>
    /// <param name="evt">The event to append.</param>
    public void AppendInput(OutputEvent evt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _inputWriter.Append(evt);
    }

    /// <summary>
    /// Appends an event to the output.jsonl file.
    /// </summary>
    /// <param name="evt">The event to append.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task AppendOutputAsync(OutputEvent evt, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _outputWriter.AppendAsync(evt, cancellationToken);
    }

    /// <summary>
    /// Appends an event to the output.jsonl file synchronously.
    /// </summary>
    /// <param name="evt">The event to append.</param>
    public void AppendOutput(OutputEvent evt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _outputWriter.Append(evt);
    }

    /// <summary>
    /// Reads all output events from the output.jsonl file.
    /// </summary>
    /// <returns>Enumerable of output events.</returns>
    public IEnumerable<OutputEvent> ReadAllOutput()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return JsonlReader.ReadAll(OutputFilePath);
    }

    /// <summary>
    /// Reads output events from the output.jsonl file starting at a specific byte offset.
    /// </summary>
    /// <param name="offset">Byte offset to start reading from.</param>
    /// <returns>Tuple of events and the new offset.</returns>
    public (IEnumerable<OutputEvent> Events, long NewOffset) ReadOutputFrom(long offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return JsonlReader.ReadFrom(OutputFilePath, offset);
    }

    /// <summary>
    /// Gets the current output file offset in bytes.
    /// </summary>
    /// <returns>Current offset.</returns>
    public long GetCurrentOutputOffset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return JsonlReader.GetFileSize(OutputFilePath);
    }

    /// <summary>
    /// Checks if the metrics.html file exists.
    /// </summary>
    /// <returns>True if metrics file exists.</returns>
    public bool HasMetrics() => File.Exists(MetricsFilePath);

    /// <summary>
    /// Reads the metrics.html file content.
    /// </summary>
    /// <returns>HTML content, or null if file doesn't exist.</returns>
    public string? ReadMetricsHtml()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(MetricsFilePath))
            return null;

        return File.ReadAllText(MetricsFilePath, Encoding.UTF8);
    }

    /// <summary>
    /// Reads the metrics.html file content asynchronously.
    /// </summary>
    /// <returns>HTML content, or null if file doesn't exist.</returns>
    public async Task<string?> ReadMetricsHtmlAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(MetricsFilePath))
            return null;

        return await File.ReadAllTextAsync(MetricsFilePath, Encoding.UTF8, cancellationToken);
    }

    /// <summary>
    /// Writes metrics HTML content to the metrics.html file.
    /// </summary>
    /// <param name="html">HTML content to write.</param>
    public void WriteMetricsHtml(string html)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(html);

        File.WriteAllText(MetricsFilePath, html, Encoding.UTF8);
    }

    /// <summary>
    /// Writes metrics HTML content to the metrics.html file asynchronously.
    /// </summary>
    /// <param name="html">HTML content to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task WriteMetricsHtmlAsync(string html, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(html);

        return File.WriteAllTextAsync(MetricsFilePath, html, Encoding.UTF8, cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _inputWriter.Dispose();
        _outputWriter.Dispose();
        _statusLock.Dispose();
    }
}
