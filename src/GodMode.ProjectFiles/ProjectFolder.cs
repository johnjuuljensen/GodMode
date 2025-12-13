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
    private const string StatusFileName = "status.json";
    private const string InputFileName = "input.jsonl";
    private const string OutputFileName = "output.jsonl";
    private const string SessionIdFileName = "session-id";
    private const string MetricsFileName = "metrics.html";
    private const string WorkDirectoryName = "work";

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
    /// Gets the path to the status.json file.
    /// </summary>
    public string StatusFilePath => Path.Combine(_projectPath, StatusFileName);

    /// <summary>
    /// Gets the path to the input.jsonl file.
    /// </summary>
    public string InputFilePath => Path.Combine(_projectPath, InputFileName);

    /// <summary>
    /// Gets the path to the output.jsonl file.
    /// </summary>
    public string OutputFilePath => Path.Combine(_projectPath, OutputFileName);

    /// <summary>
    /// Gets the path to the session-id file.
    /// </summary>
    public string SessionIdFilePath => Path.Combine(_projectPath, SessionIdFileName);

    /// <summary>
    /// Gets the path to the metrics.html file.
    /// </summary>
    public string MetricsFilePath => Path.Combine(_projectPath, MetricsFileName);

    /// <summary>
    /// Gets the path to the work directory.
    /// </summary>
    public string WorkPath => Path.Combine(_projectPath, WorkDirectoryName);

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
    /// <param name="repoUrl">Optional repository URL.</param>
    /// <returns>A new ProjectFolder instance.</returns>
    /// <exception cref="ArgumentException">Thrown when parameters are invalid.</exception>
    /// <exception cref="IOException">Thrown when project folder already exists or I/O fails.</exception>
    public static ProjectFolder Create(string rootPath, string projectId, string name, string? repoUrl = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Root path cannot be empty.", nameof(rootPath));

        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("Project ID cannot be empty.", nameof(projectId));

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Project name cannot be empty.", nameof(name));

        // Validate project ID (no invalid path characters)
        if (projectId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Project ID contains invalid characters.", nameof(projectId));

        var projectPath = Path.Combine(rootPath, projectId);

        if (Directory.Exists(projectPath))
            throw new IOException($"Project folder already exists: {projectPath}");

        // Create directory structure
        Directory.CreateDirectory(projectPath);
        Directory.CreateDirectory(Path.Combine(projectPath, WorkDirectoryName));

        // Create initial status
        var now = DateTime.UtcNow;
        var initialStatus = new ProjectStatus(
            Id: projectId,
            Name: name,
            State: ProjectState.Idle,
            CreatedAt: now,
            UpdatedAt: now,
            RepoUrl: repoUrl,
            CurrentQuestion: null,
            Metrics: new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0m),
            Git: null,
            Tests: null,
            OutputOffset: 0
        );

        var statusPath = Path.Combine(projectPath, StatusFileName);
        var statusJson = JsonSerializer.Serialize(initialStatus, ProjectJsonContext.Default.ProjectStatus);
        File.WriteAllText(statusPath, statusJson, Encoding.UTF8);

        // Create empty JSONL files
        File.WriteAllText(Path.Combine(projectPath, InputFileName), string.Empty);
        File.WriteAllText(Path.Combine(projectPath, OutputFileName), string.Empty);

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

        var statusPath = Path.Combine(projectPath, StatusFileName);
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
            await File.WriteAllTextAsync(StatusFilePath, json, Encoding.UTF8, cancellationToken);
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
            File.WriteAllText(StatusFilePath, json, Encoding.UTF8);
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
    /// Gets the Claude session ID from the session-id file.
    /// </summary>
    /// <returns>The session ID, or null if file doesn't exist.</returns>
    public string? GetSessionId()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(SessionIdFilePath))
            return null;

        return File.ReadAllText(SessionIdFilePath, Encoding.UTF8).Trim();
    }

    /// <summary>
    /// Gets the Claude session ID from the session-id file asynchronously.
    /// </summary>
    /// <returns>The session ID, or null if file doesn't exist.</returns>
    public async Task<string?> GetSessionIdAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(SessionIdFilePath))
            return null;

        var content = await File.ReadAllTextAsync(SessionIdFilePath, Encoding.UTF8, cancellationToken);
        return content.Trim();
    }

    /// <summary>
    /// Sets the Claude session ID in the session-id file.
    /// </summary>
    /// <param name="sessionId">The session ID to save.</param>
    public void SetSessionId(string sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sessionId);

        File.WriteAllText(SessionIdFilePath, sessionId, Encoding.UTF8);
    }

    /// <summary>
    /// Sets the Claude session ID in the session-id file asynchronously.
    /// </summary>
    /// <param name="sessionId">The session ID to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SetSessionIdAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sessionId);

        return File.WriteAllTextAsync(SessionIdFilePath, sessionId, Encoding.UTF8, cancellationToken);
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
