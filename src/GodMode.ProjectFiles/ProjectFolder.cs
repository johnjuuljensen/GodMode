using System.Text;
using System.Text.Json;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;


namespace GodMode.ProjectFiles;

/// <summary>
/// Represents and manages a project folder with its standard file structure.
/// </summary>
public sealed class ProjectFolder : IDisposable
{
    private const string GodModeDirectoryName = ".godmode";
    private const string StatusFileName = "status.json";
    private const string InputFileName = "input.jsonl";
    private const string OutputFileName = "output.jsonl";
    private const string MetricsFileName = "metrics.html";
    private const string GitIgnoreFileName = ".gitignore";

    /// <summary>A root's own config and scripts: <c>{root}/.godmode-root/</c>.</summary>
    public const string RootConfigFolderName = ".godmode-root";

    /// <summary>A root's script logs and result files: <c>{root}/logs/</c>.</summary>
    public const string ScriptLogsFolderName = "logs";

    /// <summary>Where archived projects went before archiving was removed; a leftover can still be on disk.</summary>
    public const string ArchivedFolderName = ".archived";

    /// <summary>
    /// The folders a root keeps for itself at its top level, which no project may be: a delete of
    /// the project would delete the root's config or every script log. Compared ignoring case, and
    /// trailing dots and spaces, as Windows compares folder names (refusing <c>LOGS</c> on Linux too).
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedFolderNames =
        new HashSet<string>([RootConfigFolderName, ScriptLogsFolderName, ArchivedFolderName], StringComparer.OrdinalIgnoreCase);

    private readonly string _projectPath;
    private readonly JsonlWriter _inputWriter;
    private readonly JsonlWriter _outputWriter;
    private readonly SemaphoreSlim _statusLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Gets the full path to the project directory.
    /// </summary>
    public string ProjectPath => _projectPath;

    /// <summary>
    /// Gets the project ID (folder name).
    /// </summary>
    public string ProjectId => Path.GetFileName(_projectPath);

    /// <summary>
    /// Gets the path to the .godmode directory where all state files are stored.
    /// </summary>
    public string GodModePath => Path.Combine(_projectPath, GodModeDirectoryName);

    /// <summary>
    /// Gets the path to the status.json file.
    /// </summary>
    public string StatusFilePath => Path.Combine(GodModePath, StatusFileName);

    /// <summary>
    /// Gets the path to the input.jsonl file.
    /// </summary>
    public string InputFilePath => Path.Combine(GodModePath, InputFileName);

    /// <summary>
    /// Gets the path to the output.jsonl file.
    /// </summary>
    public string OutputFilePath => Path.Combine(GodModePath, OutputFileName);

    /// <summary>
    /// Gets the path to the metrics.html file.
    /// </summary>
    public string MetricsFilePath => Path.Combine(GodModePath, MetricsFileName);

    private ProjectFolder(string projectPath)
    {
        _projectPath = projectPath;
        _inputWriter = new JsonlWriter(InputFilePath);
        _outputWriter = new JsonlWriter(OutputFilePath);
    }

    /// <summary>
    /// Creates a new project folder with initial files.
    /// </summary>
    /// <param name="rootPath">Root directory where project folders are stored.</param>
    /// <param name="projectId">Unique project identifier (will be used as folder name).</param>
    /// <param name="name">Human-readable project name.</param>
    /// <returns>A new ProjectFolder instance.</returns>
    /// <exception cref="ArgumentException">Thrown when parameters are invalid.</exception>
    /// <exception cref="IOException">Thrown when project folder already exists or I/O fails.</exception>
    public static ProjectFolder Create(string rootPath, string projectId, string name)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Root path cannot be empty.", nameof(rootPath));

        ValidateFolderName(projectId, nameof(projectId));

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Project name cannot be empty.", nameof(name));

        var projectPath = Path.Combine(rootPath, projectId);

        if (Directory.Exists(projectPath))
            throw new IOException($"FOLDER_EXISTS:{projectPath}");

        // Create project directory
        Directory.CreateDirectory(projectPath);
        return InitializeProjectFolder(projectPath, projectId, name);
    }

    /// <summary>
    /// Reuses an existing project directory — reinitializes .godmode state without deleting project files.
    /// </summary>
    public static ProjectFolder Reuse(string rootPath, string projectId, string name)
    {
        ValidateFolderName(projectId, nameof(projectId));
        var projectPath = Path.Combine(rootPath, projectId);
        if (!Directory.Exists(projectPath))
            throw new DirectoryNotFoundException($"Project folder not found: {projectPath}");

        return InitializeProjectFolder(projectPath, projectId, name);
    }

    /// <summary>
    /// Throws unless <paramref name="folderName"/> names a folder of its own inside the root: not
    /// empty, no path separators or other invalid characters, and not made of dots and spaces only.
    /// <c>.</c> and <c>..</c> are the root and its parent, and Windows strips trailing dots and
    /// spaces, so <c>...</c> is the root too; a delete of such a project deletes that recursively.
    /// Nor may it be one of the <see cref="ReservedFolderNames"/>.
    /// </summary>
    public static void ValidateFolderName(string? folderName, string paramName = "folderName")
    {
        if (string.IsNullOrWhiteSpace(folderName))
            throw new ArgumentException("Project folder name cannot be empty.", paramName);

        if (folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || folderName.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException($"Project folder name '{folderName}' contains invalid characters.", paramName);

        if (folderName.All(c => c is '.' or ' '))
            throw new ArgumentException($"'{folderName}' is not a valid project folder name.", paramName);

        if (ReservedFolderNames.Contains(folderName.TrimEnd('.', ' ')))
            throw new ArgumentException($"'{folderName}' is a folder the project root uses for itself.", paramName);
    }

    private static ProjectFolder InitializeProjectFolder(string projectPath, string projectId, string name)
    {
        // Create .godmode directory for all state files
        var godModePath = Path.Combine(projectPath, GodModeDirectoryName);
        Directory.CreateDirectory(godModePath);

        // Make .godmode a hidden folder on Windows
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(godModePath, File.GetAttributes(godModePath) | FileAttributes.Hidden);
        }

        // Create .gitignore in .godmode to exclude all state files from git
        var gitIgnorePath = Path.Combine(godModePath, GitIgnoreFileName);
        File.WriteAllText(gitIgnorePath, "# Exclude all GodMode state files\n*\n", Encoding.UTF8);

        // Create initial status
        var now = DateTime.UtcNow;
        var initialStatus = new ProjectStatus(
            Id: projectId,
            Name: name,
            State: ProjectState.Idle,
            CreatedAt: now,
            UpdatedAt: now,
            CurrentQuestion: null,
            Metrics: new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0m),
            Git: null,
            Tests: null,
            OutputOffset: 0
        );

        var statusPath = Path.Combine(godModePath, StatusFileName);
        var statusJson = JsonSerializer.Serialize(initialStatus, ProjectJsonContext.Default.ProjectStatus);
        File.WriteAllText(statusPath, statusJson, Encoding.UTF8);

        // Create empty JSONL files in .godmode
        File.WriteAllText(Path.Combine(godModePath, InputFileName), string.Empty);
        File.WriteAllText(Path.Combine(godModePath, OutputFileName), string.Empty);

        return new ProjectFolder(projectPath);
    }

    /// <summary>
    /// Opens an existing project folder.
    /// </summary>
    /// <param name="projectPath">Path to the project folder.</param>
    /// <returns>A ProjectFolder instance.</returns>
    /// <exception cref="DirectoryNotFoundException">Thrown when project folder doesn't exist.</exception>
    /// <exception cref="FileNotFoundException">Thrown when required files are missing.</exception>
    public static ProjectFolder Open(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            throw new ArgumentException("Project path cannot be empty.", nameof(projectPath));

        if (!Directory.Exists(projectPath))
            throw new DirectoryNotFoundException($"Project folder not found: {projectPath}");

        var godModePath = Path.Combine(projectPath, GodModeDirectoryName);
        var statusPath = Path.Combine(godModePath, StatusFileName);
        if (!File.Exists(statusPath))
            throw new FileNotFoundException($"Status file not found: {statusPath}");

        return new ProjectFolder(projectPath);
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
