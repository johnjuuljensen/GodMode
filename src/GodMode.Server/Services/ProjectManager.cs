using GodMode.Server.Models;
using GodMode.Shared.Enums;
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
    private readonly IHubContext<ProjectHub> _hubContext;
    private readonly ILogger<ProjectManager> _logger;
    private readonly ProjectFiles.ProjectManager _projectFiles;
    private readonly ConcurrentDictionary<string, ProjectInfo> _projects = new();

    public ProjectManager(
        IClaudeProcessManager processManager,
        IStatusUpdater statusUpdater,
        IHubContext<ProjectHub> hubContext,
        IConfiguration configuration,
        ILogger<ProjectManager> logger)
    {
        _processManager = processManager;
        _statusUpdater = statusUpdater;
        _hubContext = hubContext;
        _logger = logger;

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

        var metricsPath = Path.Combine(project.ProjectPath, "metrics.html");

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
                var statusPath = Path.Combine(projectPath, "status.json");
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
                var sessionIdPath = Path.Combine(projectPath, "session-id");
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
        var outputPath = Path.Combine(project.ProjectPath, "output.jsonl");

        project.OutputWatcher?.Dispose();
        project.OutputWatcher = new FileSystemWatcher(project.ProjectPath, "output.jsonl")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        project.OutputWatcher.Changed += async (sender, e) =>
        {
            await OnOutputFileChangedAsync(project);
        };
    }

    private async Task OnOutputFileChangedAsync(ProjectInfo project)
    {
        try
        {
            var outputPath = Path.Combine(project.ProjectPath, "output.jsonl");

            if (!File.Exists(outputPath))
            {
                return;
            }

            using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(project.OutputOffset, SeekOrigin.Begin);

            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var outputEvent = JsonSerializer.Deserialize<OutputEvent>(line, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (outputEvent != null)
                    {
                        // Send to subscribed clients
                        await _hubContext.Clients.Group($"project-{project.Id}")
                            .SendAsync("OutputReceived", project.Id, outputEvent);

                        // Update status based on event
                        await _statusUpdater.UpdateFromOutputEventAsync(project, outputEvent);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse output line: {Line}", line);
                }

                project.OutputOffset = stream.Position;
            }

            // Save updated offset
            await _statusUpdater.SaveStatusAsync(project);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing output file changes for project {ProjectId}", project.Id);
        }
    }

    private async Task SendOutputFromOffsetAsync(ProjectInfo project, long offset, string connectionId)
    {
        var outputPath = Path.Combine(project.ProjectPath, "output.jsonl");

        if (!File.Exists(outputPath))
        {
            return;
        }

        try
        {
            using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(offset, SeekOrigin.Begin);

            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var outputEvent = JsonSerializer.Deserialize<OutputEvent>(line, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (outputEvent != null)
                    {
                        await _hubContext.Clients.Client(connectionId)
                            .SendAsync("OutputReceived", project.Id, outputEvent);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse output line: {Line}", line);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending output from offset for project {ProjectId}", project.Id);
        }
    }

    private async Task NotifyStatusChanged(ProjectInfo project)
    {
        var status = await GetStatusAsync(project.Id);
        await _hubContext.Clients.All.SendAsync("StatusChanged", project.Id, status);
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
