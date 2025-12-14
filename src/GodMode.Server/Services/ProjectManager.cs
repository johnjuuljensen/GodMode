using GodMode.Server.Models;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using GodMode.Server.Hubs;
using System.Collections.Concurrent;
using System.Text.Json;
using ProjectFiles = GodMode.ProjectFiles;
using System.Collections.Frozen;

namespace GodMode.Server.Services;

/// <summary>
/// Manages project folders, lifecycle, and state.
/// Uses GodMode.ProjectFiles.ProjectManager for project file operations.
/// </summary>
public class ProjectManager : IProjectManager
{
    private readonly IClaudeProcessManager _processManager;
    private readonly IStatusUpdater _statusUpdater;
    private readonly IHubContext<ProjectHub, IProjectHubClient> _hubContext;
    private readonly ILogger<ProjectManager> _logger;
    private readonly ProjectFiles.ProjectManager _projectFiles;
    private readonly ConcurrentDictionary<string, ProjectInfo> _projects = new();

    public ProjectManager(
        IClaudeProcessManager processManager,
        IStatusUpdater statusUpdater,
        IHubContext<ProjectHub, IProjectHubClient> hubContext,
        IConfiguration configuration,
        ILogger<ProjectManager> logger)
    {
        _processManager = processManager;
        _statusUpdater = statusUpdater;
        _hubContext = hubContext;
        _logger = logger;

        // Subscribe to output events from Claude processes
        _processManager.OnOutputReceived += HandleOutputReceivedAsync;

        // Load project roots from configuration
        IReadOnlyDictionary<string, string> projectRoots = configuration.GetSection("ProjectRoots").Get<IReadOnlyDictionary<string, string>>() ?? FrozenDictionary<string,string>.Empty;

        _projectFiles = new ProjectFiles.ProjectManager(projectRoots);

        _logger.LogInformation("Initialized with project roots: {Roots}",
            string.Join(", ", projectRoots.Select(kvp => $"{kvp.Key}={kvp.Value}")));
    }

    public Task<ProjectRoot[]> ListProjectRootsAsync()
    {
        return Task.FromResult(_projectFiles.ListProjectRoots());
    }

    public async Task<ProjectSummary[]> ListProjectsAsync()
    {
        var summaries = new List<ProjectSummary>();

        foreach (var project in _projects.Values)
        {
            summaries.Add(new ProjectSummary(
                project.Id,
                project.Name,
                project.State,
                project.UpdatedAt,
                project.CurrentQuestion
            ));
        }

        return summaries.ToArray();
    }

    public async Task<ProjectStatus> GetStatusAsync(string projectId)
    {
        if (!_projects.TryGetValue(projectId, out var project))
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        return new ProjectStatus(
            project.Id,
            project.Name,
            project.State,
            project.CreatedAt,
            project.UpdatedAt,
            project.RepoUrl,
            project.CurrentQuestion,
            project.Metrics,
            project.Git,
            project.Tests,
            project.OutputOffset
        );
    }

    public async Task<ProjectDetail> CreateProjectAsync(CreateProjectRequest request)
    {
        _logger.LogInformation("Creating project '{Name}' of type {Type} in root '{Root}'",
            request.Name, request.ProjectType, request.ProjectRootName);

        // Use ProjectFiles to create the project structure
        var (projectFolder, projectId) = _projectFiles.CreateProject(
            request.ProjectRootName,
            request.Name,
            request.ProjectType,
            request.RepoUrl);

        var workPath = projectFolder.WorkPath;

        var project = new ProjectInfo
        {
            Id = projectId,
            Name = request.Name,
            ProjectPath = projectFolder.ProjectPath,
            WorkPath = workPath,
            RepoUrl = request.RepoUrl,
            State = ProjectState.Running,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Handle git operations based on project type
        if (request.ProjectType == ProjectType.GitHubRepo && !string.IsNullOrEmpty(request.RepoUrl))
        {
            await CloneRepositoryAsync(request.RepoUrl, workPath);
        }
        else if (request.ProjectType == ProjectType.GitHubWorktree && !string.IsNullOrEmpty(request.RepoUrl))
        {
            await SetupWorktreeAsync(request.ProjectRootName, request.Name, request.RepoUrl, workPath);
        }

        // Save initial status
        await _statusUpdater.SaveStatusAsync(project);

        // Add to tracking
        _projects[projectId] = project;

        // Setup file watcher for output.jsonl
        SetupOutputWatcher(project);

        // Start Claude process
        project.ProcessCancellation = new CancellationTokenSource();
        try
        {
            var processId = await _processManager.StartClaudeProcessAsync(
                project,
                request.InitialPrompt,
                project.ProcessCancellation.Token
            );
            project.ProcessId = processId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Claude process for project {ProjectId}", projectId);
            project.State = ProjectState.Error;
            await _statusUpdater.SaveStatusAsync(project);
            throw;
        }

        var status = await GetStatusAsync(projectId);
        return new ProjectDetail(status, project.SessionId ?? "");
    }

    public async Task SendInputAsync(string projectId, string input)
    {
        if (!_projects.TryGetValue(projectId, out var project))
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        await _processManager.SendInputAsync(project, input);

        project.State = ProjectState.Running;
        project.CurrentQuestion = null;
        project.UpdatedAt = DateTime.UtcNow;

        await _statusUpdater.SaveStatusAsync(project);
        await NotifyStatusChanged(project);
    }

    public async Task StopProjectAsync(string projectId)
    {
        if (!_projects.TryGetValue(projectId, out var project))
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        await _processManager.StopProcessAsync(project);

        project.State = ProjectState.Stopped;
        project.UpdatedAt = DateTime.UtcNow;

        await _statusUpdater.SaveStatusAsync(project);
        await NotifyStatusChanged(project);
    }

    public async Task ResumeProjectAsync(string projectId)
    {
        if (!_projects.TryGetValue(projectId, out var project))
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        // Check if already running
        if (project.State is ProjectState.Running or ProjectState.WaitingInput)
        {
            // Check if process is actually still running
            if (project.ProcessId != 0 && _processManager.IsProcessRunning(project.ProcessId))
            {
                _logger.LogInformation("Project {ProjectId} is already running with PID {ProcessId}",
                    projectId, project.ProcessId);
                return;
            }

            // Process died but state wasn't updated - fix state
            _logger.LogWarning("Project {ProjectId} was marked as {State} but process is not running, resetting state",
                projectId, project.State);
            project.State = ProjectState.Stopped;
        }

        if (project.State is not (ProjectState.Stopped or ProjectState.Idle or ProjectState.Error))
        {
            throw new InvalidOperationException($"Project {projectId} cannot be resumed (current state: {project.State})");
        }

        // Stop any existing process/cancellation token
        if (project.ProcessCancellation != null)
        {
            await project.ProcessCancellation.CancelAsync();
            project.ProcessCancellation.Dispose();
        }

        _logger.LogInformation("Resuming project {ProjectId} with session {SessionId}",
            projectId, project.SessionId);

        project.State = ProjectState.Running;
        project.UpdatedAt = DateTime.UtcNow;
        project.ProcessCancellation = new CancellationTokenSource();

        try
        {
            var processId = await _processManager.ResumeClaudeProcessAsync(
                project,
                project.ProcessCancellation.Token
            );
            project.ProcessId = processId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume Claude process for project {ProjectId}", projectId);
            project.State = ProjectState.Error;
            await _statusUpdater.SaveStatusAsync(project);
            throw;
        }

        await _statusUpdater.SaveStatusAsync(project);
        await NotifyStatusChanged(project);
    }

    public async Task SubscribeProjectAsync(string projectId, long outputOffset, string connectionId)
    {
        if (!_projects.TryGetValue(projectId, out var project))
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        project.SubscribedConnections.Add(connectionId);

        // Send any output from the requested offset
        await SendOutputFromOffsetAsync(project, outputOffset, connectionId);
    }

    public async Task UnsubscribeProjectAsync(string projectId, string connectionId)
    {
        if (!_projects.TryGetValue(projectId, out var project))
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        project.SubscribedConnections.Remove(connectionId);
    }

    public async Task<string> GetMetricsHtmlAsync(string projectId)
    {
        if (!_projects.TryGetValue(projectId, out var project))
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        var metricsPath = Path.Combine(project.ProjectPath, ".godmode", "metrics.html");

        if (File.Exists(metricsPath))
        {
            return await File.ReadAllTextAsync(metricsPath);
        }

        // Generate basic metrics HTML
        return GenerateMetricsHtml(project);
    }

    public async Task CleanupConnectionAsync(string connectionId)
    {
        foreach (var project in _projects.Values)
        {
            project.SubscribedConnections.Remove(connectionId);
        }
        await Task.CompletedTask;
    }

    public async Task RecoverProjectsAsync()
    {
        _logger.LogInformation("Recovering projects from all project roots");

        foreach (var projectPath in _projectFiles.ListProjectPaths())
        {
            try
            {
                var godModePath = Path.Combine(projectPath, ".godmode");
                var statusPath = Path.Combine(godModePath, "status.json");
                if (!File.Exists(statusPath))
                {
                    continue;
                }

                var json = await File.ReadAllTextAsync(statusPath);
                var status = JsonSerializer.Deserialize<ProjectStatus>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (status == null) continue;

                var project = new ProjectInfo
                {
                    Id = status.Id,
                    Name = status.Name,
                    ProjectPath = projectPath,
                    WorkPath = Path.Combine(projectPath, "work"),
                    RepoUrl = status.RepoUrl,
                    State = status.State == ProjectState.Running || status.State == ProjectState.WaitingInput
                        ? ProjectState.Stopped
                        : status.State,
                    CreatedAt = status.CreatedAt,
                    UpdatedAt = DateTime.UtcNow,
                    CurrentQuestion = status.CurrentQuestion,
                    Metrics = status.Metrics,
                    Git = status.Git,
                    Tests = status.Tests,
                    OutputOffset = status.OutputOffset
                };

                // Load session ID if exists
                var sessionIdPath = Path.Combine(godModePath, "session-id");
                if (File.Exists(sessionIdPath))
                {
                    project.SessionId = await File.ReadAllTextAsync(sessionIdPath);
                }

                _projects[project.Id] = project;

                // Setup file watcher
                SetupOutputWatcher(project);

                // Update status to reflect recovery
                await _statusUpdater.SaveStatusAsync(project);

                _logger.LogInformation("Recovered project {ProjectId} ({Name})", project.Id, project.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recover project from {Path}", projectPath);
            }
        }
    }

    private void SetupOutputWatcher(ProjectInfo project)
    {
        var godModePath = Path.Combine(project.ProjectPath, ".godmode");

        _logger.LogInformation("Setting up FileSystemWatcher for project {ProjectId} at {Path}",
            project.Id, godModePath);

        project.OutputWatcher?.Dispose();
        project.OutputWatcher = new FileSystemWatcher(godModePath, "output.jsonl")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        project.OutputWatcher.Changed += async (sender, e) =>
        {
            _logger.LogInformation("FileSystemWatcher triggered for project {ProjectId}, ChangeType: {ChangeType}",
                project.Id, e.ChangeType);
            await OnOutputFileChangedAsync(project);
        };

        project.OutputWatcher.Error += (sender, e) =>
        {
            _logger.LogError(e.GetException(), "FileSystemWatcher error for project {ProjectId}", project.Id);
        };

        _logger.LogInformation("FileSystemWatcher setup complete for project {ProjectId}, EnableRaisingEvents: {Enabled}",
            project.Id, project.OutputWatcher.EnableRaisingEvents);
    }

    private async Task OnOutputFileChangedAsync(ProjectInfo project)
    {
        try
        {
            var outputPath = Path.Combine(project.ProjectPath, ".godmode", "output.jsonl");

            _logger.LogInformation("OnOutputFileChangedAsync called for project {ProjectId}, checking {Path}",
                project.Id, outputPath);

            if (!File.Exists(outputPath))
            {
                _logger.LogWarning("Output file does not exist for project {ProjectId}: {Path}",
                    project.Id, outputPath);
                return;
            }

            using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(project.OutputOffset, SeekOrigin.Begin);

            _logger.LogInformation("Reading output from offset {Offset} for project {ProjectId}",
                project.OutputOffset, project.Id);

            using var reader = new StreamReader(stream);

            string? line;
            var lineCount = 0;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                lineCount++;
                _logger.LogInformation("Processing output line {LineNum} for project {ProjectId}: {Line}",
                    lineCount, project.Id, line.Length > 100 ? line[..100] + "..." : line);

                try
                {
                    var outputEvent = JsonSerializer.Deserialize<OutputEvent>(line, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (outputEvent != null)
                    {
                        _logger.LogInformation("Sending OutputReceived to group 'project-{ProjectId}', Type: {Type}",
                            project.Id, outputEvent.Type);

                        // Send to subscribed clients
                        await _hubContext.Clients.Group($"project-{project.Id}")
                            .OutputReceived(project.Id, outputEvent);

                        _logger.LogInformation("OutputReceived sent successfully for project {ProjectId}", project.Id);

                        // Update status based on event
                        await _statusUpdater.UpdateFromOutputEventAsync(project, outputEvent);
                    }
                    else
                    {
                        _logger.LogWarning("Deserialized OutputEvent is null for project {ProjectId}", project.Id);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse output line: {Line}", line);
                }

                project.OutputOffset = stream.Position;
            }

            _logger.LogInformation("Processed {LineCount} lines for project {ProjectId}, new offset: {Offset}",
                lineCount, project.Id, project.OutputOffset);

            // Save updated offset
            await _statusUpdater.SaveStatusAsync(project);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing output file changes for project {ProjectId}", project.Id);
        }
    }

    /// <summary>
    /// Handles output received directly from the Claude process manager.
    /// This bypasses the FileSystemWatcher for more reliable real-time updates.
    /// </summary>
    private async Task HandleOutputReceivedAsync(ProjectInfo project, string jsonLine)
    {
        if (string.IsNullOrWhiteSpace(jsonLine)) return;

        _logger.LogInformation("HandleOutputReceivedAsync for project {ProjectId}: {Line}",
            project.Id, jsonLine.Length > 100 ? jsonLine[..100] + "..." : jsonLine);

        try
        {
            var outputEvent = ParseClaudeOutput(jsonLine);

            if (outputEvent != null)
            {
                _logger.LogInformation("Sending OutputReceived to group 'project-{ProjectId}', Type: {Type}, Content: {Content}",
                    project.Id, outputEvent.Type, outputEvent.Content?.Length > 50 ? outputEvent.Content[..50] + "..." : outputEvent.Content);

                // Send to subscribed clients
                await _hubContext.Clients.Group($"project-{project.Id}")
                    .OutputReceived(project.Id, outputEvent);

                _logger.LogInformation("OutputReceived sent successfully for project {ProjectId}", project.Id);

                // Update status based on event
                await _statusUpdater.UpdateFromOutputEventAsync(project, outputEvent);
            }
            else
            {
                _logger.LogWarning("Parsed OutputEvent is null for project {ProjectId}", project.Id);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse output line in HandleOutputReceivedAsync: {Line}", jsonLine);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in HandleOutputReceivedAsync for project {ProjectId}", project.Id);
        }
    }

    /// <summary>
    /// Parses Claude's raw JSON output into an OutputEvent with properly extracted content.
    /// </summary>
    private OutputEvent? ParseClaudeOutput(string jsonLine)
    {
        using var doc = JsonDocument.Parse(jsonLine);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeElement))
            return null;

        var typeStr = typeElement.GetString();
        if (!Enum.TryParse<OutputEventType>(typeStr, ignoreCase: true, out var eventType))
            return null;

        var content = ExtractContent(root, eventType);
        var metadata = ExtractMetadata(root);

        return new OutputEvent(DateTime.UtcNow, eventType, content, metadata);
    }

    /// <summary>
    /// Extracts the content from Claude's JSON based on event type.
    /// </summary>
    private static string ExtractContent(JsonElement root, OutputEventType eventType)
    {
        return eventType switch
        {
            OutputEventType.User => ExtractMessageContent(root),
            OutputEventType.Assistant => ExtractMessageContent(root),
            OutputEventType.Result => ExtractResultContent(root),
            OutputEventType.System => ExtractSystemContent(root),
            OutputEventType.Error => root.TryGetProperty("error", out var err) ? err.GetString() ?? "" : "",
            _ => ""
        };
    }

    /// <summary>
    /// Extracts content from user/assistant message format: message.content[0].text
    /// </summary>
    private static string ExtractMessageContent(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message))
            return "";

        if (!message.TryGetProperty("content", out var content))
            return "";

        if (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() == 0)
            return "";

        var firstContent = content[0];
        if (firstContent.TryGetProperty("text", out var text))
            return text.GetString() ?? "";

        return "";
    }

    /// <summary>
    /// Extracts content from result format: result field
    /// </summary>
    private static string ExtractResultContent(JsonElement root)
    {
        if (root.TryGetProperty("result", out var result))
            return result.GetString() ?? "";

        return "";
    }

    /// <summary>
    /// Extracts content from system format: subtype or summary
    /// </summary>
    private static string ExtractSystemContent(JsonElement root)
    {
        if (root.TryGetProperty("subtype", out var subtype))
        {
            var subtypeStr = subtype.GetString() ?? "";
            if (root.TryGetProperty("session_id", out var sessionId))
                return $"{subtypeStr} (session: {sessionId.GetString()?[..8]}...)";
            return subtypeStr;
        }
        return "system";
    }

    /// <summary>
    /// Extracts metadata from the JSON for metrics tracking.
    /// </summary>
    private static Dictionary<string, object>? ExtractMetadata(JsonElement root)
    {
        var metadata = new Dictionary<string, object>();

        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var inputTokens))
                metadata["input_tokens"] = inputTokens.GetInt64();
            if (usage.TryGetProperty("output_tokens", out var outputTokens))
                metadata["output_tokens"] = outputTokens.GetInt64();
        }

        if (root.TryGetProperty("total_cost_usd", out var cost))
            metadata["cost_usd"] = cost.GetDouble();

        if (root.TryGetProperty("duration_ms", out var duration))
            metadata["duration_ms"] = duration.GetInt64();

        return metadata.Count > 0 ? metadata : null;
    }

    private async Task SendOutputFromOffsetAsync(ProjectInfo project, long offset, string connectionId)
    {
        var outputPath = Path.Combine(project.ProjectPath, ".godmode", "output.jsonl");

        _logger.LogInformation("SendOutputFromOffsetAsync called for project {ProjectId}, offset: {Offset}, connectionId: {ConnectionId}",
            project.Id, offset, connectionId);

        if (!File.Exists(outputPath))
        {
            _logger.LogInformation("No output file exists yet for project {ProjectId}", project.Id);
            return;
        }

        try
        {
            using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(offset, SeekOrigin.Begin);

            using var reader = new StreamReader(stream);

            string? line;
            var lineCount = 0;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                lineCount++;
                try
                {
                    var outputEvent = JsonSerializer.Deserialize<OutputEvent>(line, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (outputEvent != null)
                    {
                        _logger.LogInformation("Sending existing output line {LineNum} to client {ConnectionId} for project {ProjectId}, Type: {Type}",
                            lineCount, connectionId, project.Id, outputEvent.Type);

                        await _hubContext.Clients.Client(connectionId)
                            .OutputReceived(project.Id, outputEvent);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse output line: {Line}", line);
                }
            }

            _logger.LogInformation("Sent {LineCount} existing output lines to client {ConnectionId} for project {ProjectId}",
                lineCount, connectionId, project.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending output from offset for project {ProjectId}", project.Id);
        }
    }

    private async Task NotifyStatusChanged(ProjectInfo project)
    {
        var status = await GetStatusAsync(project.Id);
        await _hubContext.Clients.All.StatusChanged(project.Id, status);
    }

    private async Task CloneRepositoryAsync(string repoUrl, string workPath)
    {
        _logger.LogInformation("Cloning repository {RepoUrl} to {WorkPath}", repoUrl, workPath);

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"clone {repoUrl} .",
                WorkingDirectory = workPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync();
            throw new Exception($"Failed to clone repository: {error}");
        }
    }

    private async Task SetupWorktreeAsync(string rootName, string branchName, string repoUrl, string workPath)
    {
        var rootPath = _projectFiles.GetProjectRootPath(rootName);
        var bareRepoPath = ProjectFiles.ProjectManager.GetBareRepoPath(rootPath, repoUrl);

        _logger.LogInformation("Setting up worktree for branch '{Branch}' from {RepoUrl}", branchName, repoUrl);

        // Check if bare repo exists, if not clone it
        if (!Directory.Exists(bareRepoPath))
        {
            _logger.LogInformation("Cloning bare repository to {BareRepoPath}", bareRepoPath);

            var cloneProcess = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"clone --bare {repoUrl} \"{bareRepoPath}\"",
                    WorkingDirectory = rootPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            cloneProcess.Start();
            await cloneProcess.WaitForExitAsync();

            if (cloneProcess.ExitCode != 0)
            {
                var error = await cloneProcess.StandardError.ReadToEndAsync();
                throw new Exception($"Failed to clone bare repository: {error}");
            }
        }

        // Fetch latest changes
        var fetchProcess = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "fetch --all",
                WorkingDirectory = bareRepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        fetchProcess.Start();
        await fetchProcess.WaitForExitAsync();

        // Create worktree for the branch
        _logger.LogInformation("Creating worktree at {WorkPath} for branch {Branch}", workPath, branchName);

        var worktreeProcess = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"worktree add -b {branchName} \"{workPath}\" origin/main",
                WorkingDirectory = bareRepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        worktreeProcess.Start();
        await worktreeProcess.WaitForExitAsync();

        if (worktreeProcess.ExitCode != 0)
        {
            var error = await worktreeProcess.StandardError.ReadToEndAsync();
            // If branch already exists, try to add worktree without creating branch
            if (error.Contains("already exists"))
            {
                worktreeProcess = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = $"worktree add \"{workPath}\" {branchName}",
                        WorkingDirectory = bareRepoPath,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                worktreeProcess.Start();
                await worktreeProcess.WaitForExitAsync();

                if (worktreeProcess.ExitCode != 0)
                {
                    error = await worktreeProcess.StandardError.ReadToEndAsync();
                    throw new Exception($"Failed to create worktree: {error}");
                }
            }
            else
            {
                throw new Exception($"Failed to create worktree: {error}");
            }
        }
    }

    private string GenerateMetricsHtml(ProjectInfo project)
    {
        return $@"
<!DOCTYPE html>
<html>
<head>
    <title>Metrics - {project.Name}</title>
    <style>
        body {{ font-family: Arial, sans-serif; margin: 20px; }}
        .metric {{ margin: 10px 0; }}
        .label {{ font-weight: bold; }}
    </style>
</head>
<body>
    <h1>Project Metrics: {project.Name}</h1>
    <div class=""metric"">
        <span class=""label"">Input Tokens:</span> {project.Metrics.InputTokens:N0}
    </div>
    <div class=""metric"">
        <span class=""label"">Output Tokens:</span> {project.Metrics.OutputTokens:N0}
    </div>
    <div class=""metric"">
        <span class=""label"">Tool Calls:</span> {project.Metrics.ToolCalls}
    </div>
    <div class=""metric"">
        <span class=""label"">Duration:</span> {project.Metrics.Duration}
    </div>
    <div class=""metric"">
        <span class=""label"">Cost Estimate:</span> ${project.Metrics.CostEstimate:F4}
    </div>
</body>
</html>";
    }
}
