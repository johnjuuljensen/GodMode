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
/// Uses config-driven workflow: reads .godmode-root.json, runs scripts, starts Claude.
/// </summary>
public class ProjectManager : IProjectManager
{
    private readonly IClaudeProcessManager _processManager;
    private readonly IStatusUpdater _statusUpdater;
    private readonly IRootConfigReader _rootConfigReader;
    private readonly IScriptRunner _scriptRunner;
    private readonly IHubContext<ProjectHub, IProjectHubClient> _hubContext;
    private readonly ILogger<ProjectManager> _logger;
    private readonly ProjectFiles.ProjectManager _projectFiles;
    private readonly ConcurrentDictionary<string, ProjectInfo> _projects = new();

    public ProjectManager(
        IClaudeProcessManager processManager,
        IStatusUpdater statusUpdater,
        IRootConfigReader rootConfigReader,
        IScriptRunner scriptRunner,
        IHubContext<ProjectHub, IProjectHubClient> hubContext,
        IConfiguration configuration,
        ILogger<ProjectManager> logger)
    {
        _processManager = processManager;
        _statusUpdater = statusUpdater;
        _rootConfigReader = rootConfigReader;
        _scriptRunner = scriptRunner;
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

    public Task<ProjectRootInfo[]> ListProjectRootsAsync()
    {
        var roots = _projectFiles.ProjectRoots.Select(kvp =>
        {
            var config = _rootConfigReader.ReadConfig(kvp.Value);
            return new ProjectRootInfo(kvp.Key, config.Description, config.InputSchema);
        }).ToArray();

        return Task.FromResult(roots);
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
            project.CurrentQuestion,
            project.Metrics,
            project.Git,
            project.Tests,
            project.OutputOffset
        );
    }

    public async Task<ProjectDetail> CreateProjectAsync(CreateProjectRequest request)
    {
        _logger.LogInformation("Creating project in root '{Root}' with inputs: {InputKeys}",
            request.ProjectRootName, string.Join(", ", request.Inputs.Keys));

        // Read root config
        var rootPath = _projectFiles.GetProjectRootPath(request.ProjectRootName);
        var config = _rootConfigReader.ReadConfig(rootPath);

        // Resolve name from inputs or nameTemplate
        var name = ResolveProjectName(config, request.Inputs);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Project name is required. Provide a 'name' input or configure nameTemplate in .godmode-root.json.");

        // Resolve prompt from inputs or promptTemplate
        var prompt = ResolvePrompt(config, request.Inputs);

        // Create project folder — either server-managed or script-managed
        var projectId = ProjectFiles.ProjectManager.ConvertNameToPath(name);
        string projectPath;

        if (config.ScriptsCreateFolder)
        {
            // Scripts will create the project directory (e.g. git worktree add)
            projectPath = Path.Combine(rootPath, projectId);
        }
        else
        {
            // Server creates the project folder via ProjectFiles
            var (projectFolder, _) = _projectFiles.CreateProject(request.ProjectRootName, name);
            projectPath = projectFolder.ProjectPath;
        }

        var project = new ProjectInfo
        {
            Id = projectId,
            Name = name,
            ProjectPath = projectPath,
            RootName = request.ProjectRootName,
            State = ProjectState.Running,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Build environment variables for scripts
        var scriptEnv = BuildScriptEnvironment(rootPath, project, config, request.Inputs);

        // Script log file — at root level so it persists regardless of what scripts do
        var logFilePath = GetScriptLogPath(rootPath, projectId);

        // Run setup scripts (always runs in root directory)
        if (config.Setup is { Length: > 0 })
        {
            try
            {
                await _scriptRunner.RunAsync(
                    config.Setup,
                    rootPath,
                    rootPath,
                    scriptEnv,
                    msg => _hubContext.Clients.All.CreationProgress(projectId, msg),
                    logFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Setup script failed for project {ProjectId}. See log: {LogPath}", projectId, logFilePath);
                project.State = ProjectState.Error;
                EnsureGodModeDirectory(projectPath);
                await _statusUpdater.SaveStatusAsync(project);
                _projects[projectId] = project;
                throw;
            }
        }

        // Run bootstrap scripts
        if (config.Bootstrap is { Length: > 0 })
        {
            // When scripts create the folder, bootstrap runs from root (project dir may not exist yet)
            var bootstrapWorkDir = config.ScriptsCreateFolder ? rootPath : projectPath;
            try
            {
                await _scriptRunner.RunAsync(
                    config.Bootstrap,
                    rootPath,
                    bootstrapWorkDir,
                    scriptEnv,
                    msg => _hubContext.Clients.All.CreationProgress(projectId, msg),
                    logFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bootstrap script failed for project {ProjectId}. See log: {LogPath}", projectId, logFilePath);
                project.State = ProjectState.Error;
                EnsureGodModeDirectory(projectPath);
                await _statusUpdater.SaveStatusAsync(project);
                _projects[projectId] = project;
                throw;
            }
        }

        // Ensure .godmode directory exists (scripts may have created the project dir without it)
        EnsureGodModeDirectory(projectPath);

        // Save project settings (persists across restarts)
        var skipPermissions = GetBool(request.Inputs, "skipPermissions");
        var settings = new ProjectFiles.ProjectSettings(DangerouslySkipPermissions: skipPermissions);
        settings.Save(projectPath);

        // Save initial status
        await _statusUpdater.SaveStatusAsync(project);

        // Add to tracking
        _projects[projectId] = project;

        // Build claude env/args from root config + project settings
        var (claudeEnv, claudeArgs) = BuildClaudeConfig(config, settings);

        // Start Claude process
        project.ProcessCancellation = new CancellationTokenSource();
        try
        {
            var processId = await _processManager.StartClaudeProcessAsync(
                project,
                prompt ?? "Hello",
                project.ProcessCancellation.Token,
                claudeEnv,
                claudeArgs
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

    public async Task DeleteProjectAsync(string projectId)
    {
        if (!_projects.TryGetValue(projectId, out var project))
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        _logger.LogInformation("Deleting project {ProjectId} ({Name})", projectId, project.Name);

        // Stop Claude process if running
        await _processManager.StopProcessAsync(project);

        // Run teardown scripts if configured
        if (project.RootName != null)
        {
            try
            {
                var rootPath = _projectFiles.GetProjectRootPath(project.RootName);
                var config = _rootConfigReader.ReadConfig(rootPath);

                if (config.Teardown is { Length: > 0 })
                {
                    var scriptEnv = BuildScriptEnvironment(rootPath, project, config, new Dictionary<string, JsonElement>());

                    await _scriptRunner.RunAsync(
                        config.Teardown,
                        rootPath,
                        project.ProjectPath,
                        scriptEnv,
                        msg => _hubContext.Clients.All.CreationProgress(projectId, msg));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Teardown script failed for project {ProjectId}, continuing with deletion", projectId);
            }
        }

        // Remove from tracking
        _projects.TryRemove(projectId, out _);

        // Delete project folder
        _projectFiles.DeleteProject(projectId, force: true);

        _logger.LogInformation("Project {ProjectId} deleted successfully", projectId);
    }

    public async Task ResumeProjectAsync(string projectId)
    {
        if (!_projects.TryGetValue(projectId, out var project))
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        // Check if process is actually still running (regardless of reported state)
        if (project.ProcessId != 0 && _processManager.IsProcessRunning(project.ProcessId))
        {
            _logger.LogInformation("Project {ProjectId} already has a running process with PID {ProcessId} (state: {State})",
                projectId, project.ProcessId, project.State);

            if (project.State == ProjectState.Idle)
            {
                _logger.LogInformation("Project {ProjectId} is idle with running process, sending continue prompt", projectId);
                await _processManager.SendInputAsync(project, "Continue");
                project.State = ProjectState.Running;
                project.UpdatedAt = DateTime.UtcNow;
                await _statusUpdater.SaveStatusAsync(project);
                await NotifyStatusChanged(project);
                return;
            }

            return;
        }

        // Process is not running - check if state needs correction
        if (project.State is ProjectState.Running or ProjectState.WaitingInput)
        {
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

        // Build claude env/args from root config + persisted project settings
        Dictionary<string, string>? claudeEnv = null;
        string[]? claudeArgs = null;
        try
        {
            var settings = ProjectFiles.ProjectSettings.Load(project.ProjectPath);
            if (project.RootName != null)
            {
                var rootPath = _projectFiles.GetProjectRootPath(project.RootName);
                var config = _rootConfigReader.ReadConfig(rootPath);
                (claudeEnv, claudeArgs) = BuildClaudeConfig(config, settings);
            }
            else
            {
                // No root config, just apply project settings
                (_, claudeArgs) = BuildClaudeConfig(new RootConfig(), settings);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read config for project {ProjectId}, continuing without extra config", projectId);
        }

        try
        {
            var processId = await _processManager.ResumeClaudeProcessAsync(
                project,
                project.ProcessCancellation.Token,
                claudeEnv,
                claudeArgs
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

    private static readonly JsonSerializerOptions CaseInsensitiveOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task RecoverProjectsAsync()
    {
        _logger.LogInformation("Recovering projects from all project roots");

        var projectPaths = _projectFiles.ListProjectPaths().ToList();

        // Process all projects in parallel for faster startup
        await Parallel.ForEachAsync(projectPaths, async (projectPath, ct) =>
        {
            try
            {
                var godModePath = Path.Combine(projectPath, ".godmode");
                var statusPath = Path.Combine(godModePath, "status.json");
                if (!File.Exists(statusPath))
                {
                    return;
                }

                var json = await File.ReadAllTextAsync(statusPath, ct);
                var status = JsonSerializer.Deserialize<ProjectStatus>(json, CaseInsensitiveOptions);

                if (status == null) return;

                // Check if state needs to be corrected (was running when server stopped)
                var stateChanged = status.State is ProjectState.Running or ProjectState.WaitingInput;

                var project = new ProjectInfo
                {
                    Id = status.Id,
                    Name = status.Name,
                    ProjectPath = projectPath,
                    State = stateChanged ? ProjectState.Stopped : status.State,
                    CreatedAt = status.CreatedAt,
                    UpdatedAt = stateChanged ? DateTime.UtcNow : status.UpdatedAt,
                    CurrentQuestion = status.CurrentQuestion,
                    Metrics = status.Metrics,
                    Git = status.Git,
                    Tests = status.Tests,
                    OutputOffset = status.OutputOffset
                };

                // Try to determine which root this project belongs to
                foreach (var (rootName, rootPath) in _projectFiles.ProjectRoots)
                {
                    if (projectPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                    {
                        project.RootName = rootName;
                        break;
                    }
                }

                // Load session ID if exists
                var sessionIdPath = Path.Combine(godModePath, "session-id");
                if (File.Exists(sessionIdPath))
                {
                    project.SessionId = await File.ReadAllTextAsync(sessionIdPath, ct);
                }

                _projects[project.Id] = project;

                // Only save if state changed
                if (stateChanged)
                {
                    await _statusUpdater.SaveStatusAsync(project);
                }

                _logger.LogInformation("Recovered project {ProjectId} ({Name})", project.Id, project.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recover project from {Path}", projectPath);
            }
        });
    }

    /// <summary>
    /// Ensures the .godmode directory exists in a project folder.
    /// Called after scripts run (which may have created the project dir without .godmode).
    /// </summary>
    private static void EnsureGodModeDirectory(string projectPath)
    {
        var godModePath = Path.Combine(projectPath, ".godmode");
        if (Directory.Exists(godModePath)) return;

        Directory.CreateDirectory(godModePath);
        File.WriteAllText(
            Path.Combine(godModePath, ".gitignore"),
            "# Exclude all GodMode state files\n*\n",
            System.Text.Encoding.UTF8);
    }

    /// <summary>
    /// Returns a log file path at the root level for script output.
    /// Uses {rootPath}/logs/{projectId}.log so it survives bootstrap's project dir delete.
    /// </summary>
    private static string GetScriptLogPath(string rootPath, string projectId)
    {
        var logsDir = Path.Combine(rootPath, "logs");
        Directory.CreateDirectory(logsDir);
        return Path.Combine(logsDir, $"{projectId}.log");
    }

    /// <summary>
    /// Resolves the project name from inputs or nameTemplate.
    /// </summary>
    private static string? ResolveProjectName(RootConfig config, Dictionary<string, JsonElement> inputs)
    {
        if (config.NameTemplate != null)
            return TemplateResolver.Resolve(config.NameTemplate, inputs);

        return TemplateResolver.GetString(inputs, "name");
    }

    /// <summary>
    /// Resolves the initial prompt from inputs or promptTemplate.
    /// </summary>
    private static string? ResolvePrompt(RootConfig config, Dictionary<string, JsonElement> inputs)
    {
        if (config.PromptTemplate != null)
            return TemplateResolver.Resolve(config.PromptTemplate, inputs);

        return TemplateResolver.GetString(inputs, "prompt");
    }

    /// <summary>
    /// Gets a boolean value from inputs. Handles both JsonValueKind.True/False and string "true"/"false".
    /// </summary>
    private static bool GetBool(Dictionary<string, JsonElement> inputs, string key)
    {
        if (!inputs.TryGetValue(key, out var value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString() is "true" or "True",
            _ => false
        };
    }

    /// <summary>
    /// Builds claude environment and args from root config + project settings.
    /// </summary>
    private static (Dictionary<string, string>? Env, string[]? Args) BuildClaudeConfig(
        RootConfig config, ProjectFiles.ProjectSettings settings)
    {
        var env = config.Environment != null
            ? new Dictionary<string, string>(config.Environment)
            : null;

        if (!string.IsNullOrEmpty(config.ClaudeConfigDir))
        {
            env ??= new Dictionary<string, string>();
            env["CLAUDE_CONFIG_DIR"] = config.ClaudeConfigDir;
        }

        var args = new List<string>();
        if (config.ClaudeArgs != null)
            args.AddRange(config.ClaudeArgs);
        if (settings.DangerouslySkipPermissions)
            args.Add("--dangerously-skip-permissions");

        return (env, args.Count > 0 ? args.ToArray() : null);
    }

    /// <summary>
    /// Builds the full environment variables dictionary for scripts.
    /// </summary>
    private static Dictionary<string, string> BuildScriptEnvironment(
        string rootPath,
        ProjectInfo project,
        RootConfig config,
        Dictionary<string, JsonElement> inputs)
    {
        var env = new Dictionary<string, string>
        {
            ["GODMODE_ROOT_PATH"] = rootPath,
            ["GODMODE_PROJECT_PATH"] = project.ProjectPath,
            ["GODMODE_PROJECT_ID"] = project.Id,
            ["GODMODE_PROJECT_NAME"] = project.Name
        };

        // Add root config environment
        if (config.Environment != null)
        {
            foreach (var (key, value) in config.Environment)
                env[key] = value;
        }

        // Add form inputs as GODMODE_INPUT_* env vars
        foreach (var (key, value) in inputs)
        {
            var envKey = "GODMODE_INPUT_" + ToUpperSnakeCase(key);
            env[envKey] = value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : value.ToString();
        }

        return env;
    }

    /// <summary>
    /// Converts camelCase/PascalCase to UPPER_SNAKE_CASE.
    /// </summary>
    private static string ToUpperSnakeCase(string input)
    {
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsUpper(c) && i > 0)
                result.Append('_');
            result.Append(char.ToUpperInvariant(c));
        }
        return result.ToString();
    }

    /// <summary>
    /// Handles output received directly from the Claude process manager.
    /// Sends raw JSON to clients for UI parsing/rendering.
    /// </summary>
    private async Task HandleOutputReceivedAsync(ProjectInfo project, string jsonLine)
    {
        if (string.IsNullOrWhiteSpace(jsonLine)) return;

        _logger.LogInformation("HandleOutputReceivedAsync for project {ProjectId}: {Line}",
            project.Id, jsonLine.Length > 100 ? jsonLine[..100] + "..." : jsonLine);

        try
        {
            // Extract type for logging and status updates
            var eventType = ExtractEventType(jsonLine);

            _logger.LogInformation("Sending raw JSON to group 'project-{ProjectId}', Type: {Type}",
                project.Id, eventType);

            // Send raw JSON to subscribed clients - UI will parse and render
            await _hubContext.Clients.Group($"project-{project.Id}")
                .OutputReceived(project.Id, jsonLine);

            _logger.LogInformation("OutputReceived sent successfully for project {ProjectId}", project.Id);

            // Update status based on event (still need to parse for status updates)
            if (eventType != null)
            {
                var outputEvent = ParseClaudeOutput(jsonLine);
                if (outputEvent != null)
                {
                    await _statusUpdater.UpdateFromOutputEventAsync(project, outputEvent);
                }
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
    /// Extracts the event type from raw JSON for logging purposes.
    /// </summary>
    private static string? ExtractEventType(string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            if (doc.RootElement.TryGetProperty("type", out var typeElement))
            {
                return typeElement.GetString();
            }
        }
        catch { }
        return null;
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

    private static string ExtractResultContent(JsonElement root)
    {
        if (root.TryGetProperty("result", out var result))
            return result.GetString() ?? "";

        return "";
    }

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

                var eventType = ExtractEventType(line);
                _logger.LogInformation("Sending existing output line {LineNum} to client {ConnectionId} for project {ProjectId}, Type: {Type}",
                    lineCount, connectionId, project.Id, eventType);

                // Send raw JSON to client - UI will parse and render
                await _hubContext.Clients.Client(connectionId)
                    .OutputReceived(project.Id, line);
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
