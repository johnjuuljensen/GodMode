using GodMode.Server.Models;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using GodMode.Server.Hubs;
using System.Collections.Concurrent;
using System.Text.Json;
using ProjectFiles = GodMode.ProjectFiles;

namespace GodMode.Server.Services;

/// <summary>
/// Manages project folders, lifecycle, and state.
/// Uses config-driven workflow: reads .godmode-root/config.json, runs scripts, starts Claude.
/// </summary>
public class ProjectManager : IProjectManager
{
    private readonly IClaudeProcessManager _processManager;
    private readonly IStatusUpdater _statusUpdater;
    private readonly IRootConfigReader _rootConfigReader;
    private readonly IScriptRunner _scriptRunner;
    private readonly IHubContext<ProjectHub, IProjectHubClient> _hubContext;
    private readonly ILogger<ProjectManager> _logger;
    private readonly ConcurrentDictionary<string, ProjectInfo> _projects = new();

    /// <summary>
    /// Profiles loaded from appsettings.json (static, never changes at runtime).
    /// </summary>
    private readonly Dictionary<string, ProfileConfig> _explicitProfiles;

    /// <summary>
    /// Optional directory to scan for autodiscovered roots.
    /// Null when autodiscovery is disabled.
    /// </summary>
    private readonly string? _projectRootsDir;

    /// <summary>
    /// Lock for rebuilding the profile snapshot.
    /// </summary>
    private readonly object _profileLock = new();

    /// <summary>
    /// Immutable snapshot of all profile/root state. Swapped atomically via volatile.
    /// Readers capture the reference once to get a consistent view.
    /// </summary>
    private volatile ProfileSnapshot _snapshot;

    /// <summary>
    /// Immutable snapshot of merged profile and root lookup state.
    /// </summary>
    private sealed record ProfileSnapshot(
        Dictionary<string, ProfileConfig> Profiles,
        Dictionary<(string, string), string> RootLookup,
        Dictionary<string, (string, string)> PathToProfileRoot,
        ProjectFiles.ProjectManager ProjectFiles);

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

        // Read optional autodiscovery directory (normalize empty/whitespace to null)
        var rawDir = configuration["ProjectRootsDir"];
        _projectRootsDir = string.IsNullOrWhiteSpace(rawDir) ? null : rawDir;

        // Load explicit profiles from configuration (with backward compat for ProjectRoots)
        _explicitProfiles = LoadProfiles(configuration, hasAutoDiscovery: _projectRootsDir != null);

        if (_projectRootsDir != null)
            _logger.LogInformation("Autodiscovery enabled: scanning {ProjectRootsDir} for .godmode-root/ directories", _projectRootsDir);

        // Build initial profile/root snapshot
        _snapshot = BuildSnapshot();
    }

    private static Dictionary<string, ProfileConfig> LoadProfiles(IConfiguration configuration, bool hasAutoDiscovery)
    {
        var profiles = configuration.GetSection("Profiles").Get<Dictionary<string, ProfileConfig>>();

        if (profiles is { Count: > 0 })
            return profiles;

        // Backward compat: map old ProjectRoots → "Default" profile
        var legacyRoots = configuration.GetSection("ProjectRoots").Get<Dictionary<string, string>>();
        if (legacyRoots is { Count: > 0 })
        {
            return new Dictionary<string, ProfileConfig>
            {
                ["Default"] = new ProfileConfig { Roots = legacyRoots }
            };
        }

        // No explicit config — return empty if autodiscovery will provide roots,
        // otherwise fall back to a default "projects" root.
        if (hasAutoDiscovery)
            return new Dictionary<string, ProfileConfig>();

        return new Dictionary<string, ProfileConfig>
        {
            ["Default"] = new ProfileConfig
            {
                Roots = new Dictionary<string, string> { ["default"] = "projects" }
            }
        };
    }

    /// <summary>
    /// Builds an immutable snapshot of all profile/root state by merging explicit profiles
    /// with autodiscovered roots. Thread-safe — can be called from any thread.
    /// </summary>
    private ProfileSnapshot BuildSnapshot()
    {
        // Start with explicit profiles
        var merged = new Dictionary<string, ProfileConfig>(_explicitProfiles, StringComparer.OrdinalIgnoreCase);

        // Discover roots from ProjectRootsDir (if configured)
        if (_projectRootsDir != null)
        {
            var discovered = DiscoverProfiles(_projectRootsDir);
            foreach (var (profileName, profileConfig) in discovered)
            {
                if (merged.TryGetValue(profileName, out var existing))
                {
                    // Explicit profiles take precedence — merge only new root names
                    var mergedRoots = new Dictionary<string, string>(existing.Roots, StringComparer.OrdinalIgnoreCase);
                    foreach (var (rootName, rootPath) in profileConfig.Roots)
                    {
                        mergedRoots.TryAdd(rootName, rootPath);
                    }
                    merged[profileName] = new ProfileConfig
                    {
                        Roots = mergedRoots,
                        Environment = existing.Environment,
                        Description = existing.Description ?? profileConfig.Description
                    };
                }
                else
                {
                    merged[profileName] = profileConfig;
                }
            }
        }

        // If still empty after discovery, create a default
        if (merged.Count == 0)
        {
            merged["Default"] = new ProfileConfig
            {
                Roots = new Dictionary<string, string> { ["default"] = "projects" }
            };
        }

        var (rootLookup, pathToProfileRoot) = BuildRootLookups(merged);

        // Build ProjectFiles.ProjectManager with the merged root set
        var compositeRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ((profile, root), path) in rootLookup)
        {
            compositeRoots[$"{profile}/{root}"] = path;
        }
        var projectFiles = compositeRoots.Count > 0
            ? new ProjectFiles.ProjectManager(compositeRoots)
            : new ProjectFiles.ProjectManager("projects");

        _logger.LogInformation("Profile snapshot: {ProfileCount} profiles, {RootCount} total roots: {Roots}",
            merged.Count,
            rootLookup.Count,
            string.Join(", ", rootLookup.Select(kvp => $"{kvp.Key.Item1}/{kvp.Key.Item2}={kvp.Value}")));

        return new ProfileSnapshot(merged, rootLookup, pathToProfileRoot, projectFiles);
    }

    /// <summary>
    /// Rebuilds the profile snapshot atomically.
    /// Uses a lock to prevent concurrent rebuilds from wasting work.
    /// </summary>
    private void RebuildSnapshot()
    {
        lock (_profileLock)
        {
            _snapshot = BuildSnapshot();
        }
    }

    /// <summary>
    /// Scans a directory for subdirectories containing .godmode-root/ and builds profiles from them.
    /// Roots with the same profileName in config.json are grouped into one profile.
    /// Roots without profileName become their own single-root profile (named after the directory).
    /// </summary>
    private Dictionary<string, ProfileConfig> DiscoverProfiles(string rootsDir)
    {
        var fullPath = Path.GetFullPath(rootsDir);
        if (!Directory.Exists(fullPath))
        {
            _logger.LogDebug("ProjectRootsDir {RootsDir} does not exist, skipping autodiscovery", fullPath);
            return new Dictionary<string, ProfileConfig>();
        }

        var profiles = new Dictionary<string, ProfileConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (var subDir in Directory.GetDirectories(fullPath))
        {
            var godModeRootDir = Path.Combine(subDir, ".godmode-root");
            if (!Directory.Exists(godModeRootDir))
                continue;

            var dirName = Path.GetFileName(subDir);
            try
            {
                var config = _rootConfigReader.ReadConfig(subDir);
                var profileName = config.ProfileName ?? dirName;

                if (!profiles.TryGetValue(profileName, out var existingProfile))
                {
                    existingProfile = new ProfileConfig
                    {
                        Roots = new Dictionary<string, string>(),
                        Description = config.Description
                    };
                    profiles[profileName] = existingProfile;
                }

                existingProfile.Roots[dirName] = subDir;
                _logger.LogDebug("Discovered root '{RootName}' → profile '{ProfileName}' at {Path}", dirName, profileName, subDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read config for discovered root at {Path}, skipping", subDir);
            }
        }

        return profiles;
    }

    private static (Dictionary<(string, string), string>, Dictionary<string, (string, string)>) BuildRootLookups(
        Dictionary<string, ProfileConfig> profiles)
    {
        var rootLookup = new Dictionary<(string, string), string>(TupleComparer.Instance);
        var pathLookup = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (profileName, config) in profiles)
        {
            foreach (var (rootName, rootPath) in config.Roots)
            {
                rootLookup[(profileName, rootName)] = rootPath;
                pathLookup[Path.GetFullPath(rootPath)] = (profileName, rootName);
            }
        }

        return (rootLookup, pathLookup);
    }

    private sealed class TupleComparer : IEqualityComparer<(string, string)>
    {
        public static readonly TupleComparer Instance = new();
        public bool Equals((string, string) x, (string, string) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Item1, y.Item1) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Item2, y.Item2);
        public int GetHashCode((string, string) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2));
    }

    /// <summary>
    /// Gets the composite key used in ProjectFiles.ProjectManager for a (profile, root) pair.
    /// </summary>
    private static string CompositeKey(string profile, string root) => $"{profile}/{root}";

    public Task<ProfileInfo[]> ListProfilesAsync()
    {
        // Rebuild to pick up newly added autodiscovered roots
        if (_projectRootsDir != null)
            RebuildSnapshot();

        var snap = _snapshot;
        var profiles = snap.Profiles.Select(kvp =>
            new ProfileInfo(kvp.Key, kvp.Value.Description)
        ).ToArray();

        return Task.FromResult(profiles);
    }

    public Task<ProjectRootInfo[]> ListProjectRootsAsync()
    {
        // Rebuild to pick up newly added autodiscovered roots
        if (_projectRootsDir != null)
            RebuildSnapshot();

        var snap = _snapshot;
        var roots = snap.RootLookup.Select(kvp =>
        {
            var (profileName, rootName) = kvp.Key;
            var rootPath = kvp.Value;
            var resolvedPath = snap.ProjectFiles.GetProjectRootPath(CompositeKey(profileName, rootName));
            var config = _rootConfigReader.ReadConfig(resolvedPath);
            var actions = config.GetEffectiveActions()
                .Select(a => new CreateActionInfo(a.Name, a.Description, a.InputSchema, a.Model))
                .ToArray();
            return new ProjectRootInfo(rootName, config.Description, actions, ProfileName: profileName);
        }).ToArray();

        return Task.FromResult(roots);
    }

    public async Task<ProjectSummary[]> ListProjectsAsync()
    {
        var summaries = new List<ProjectSummary>();

        foreach (var project in _projects.Values)
        {
            var s = project.Status;
            summaries.Add(new ProjectSummary(
                s.Id,
                s.Name,
                s.State,
                s.UpdatedAt,
                s.CurrentQuestion,
                s.RootName,
                ProfileName: project.ProfileName ?? s.ProfileName
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

        return project.Status;
    }

    public async Task<ProjectStatus> CreateProjectAsync(CreateProjectRequest request)
    {
        _logger.LogInformation("Creating project in profile '{Profile}' root '{Root}' action '{Action}' with inputs: {InputKeys}",
            request.ProfileName, request.ProjectRootName, request.ActionName ?? "(default)", string.Join(", ", request.Inputs.Keys));

        // Capture snapshot for consistent state throughout this operation
        var snap = _snapshot;

        // Resolve root path via profile lookup
        var compositeKey = CompositeKey(request.ProfileName, request.ProjectRootName);
        var rootPath = snap.ProjectFiles.GetProjectRootPath(compositeKey);
        var config = _rootConfigReader.ReadConfig(rootPath);
        var action = config.ResolveAction(request.ActionName)
            ?? throw new ArgumentException($"Action '{request.ActionName}' not found in root '{request.ProjectRootName}'.");

        // Get profile environment for merging
        snap.Profiles.TryGetValue(request.ProfileName, out var profileConfig);
        var profileEnv = profileConfig?.Environment;

        // Resolve name from inputs or nameTemplate
        var name = ResolveProjectName(action, request.Inputs);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Project name is required. Provide a 'name' input or configure nameTemplate in .godmode-root/config.json.");

        // Resolve prompt from inputs or promptTemplate
        var prompt = ResolvePrompt(action, request.Inputs);

        // Create project folder — either server-managed or script-managed
        var projectId = ProjectFiles.ProjectManager.ConvertNameToPath(name);
        string projectPath;

        if (action.ScriptsCreateFolder)
        {
            // Scripts will create the project directory (e.g. git worktree add)
            projectPath = Path.Combine(rootPath, projectId);
        }
        else
        {
            // Server creates the project folder via ProjectFiles
            var (projectFolder, _) = snap.ProjectFiles.CreateProject(compositeKey, name);
            projectPath = projectFolder.ProjectPath;
        }

        var now = DateTime.UtcNow;
        var project = new ProjectInfo
        {
            Status = new ProjectStatus(
                projectId,
                name,
                ProjectState.Running,
                now,
                now,
                CurrentQuestion: null,
                new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0),
                Git: null,
                Tests: null,
                OutputOffset: 0,
                RootName: request.ProjectRootName,
                ProfileName: request.ProfileName
            ),
            ProjectPath = projectPath,
            ActionName = action.Name,
            ProfileName = request.ProfileName
        };

        // Result file — scripts can write key=value pairs to override project path/name
        var resultFilePath = GetResultFilePath(rootPath, projectId);
        if (File.Exists(resultFilePath)) File.Delete(resultFilePath);

        // Build environment variables for scripts (profile env merged in)
        var scriptEnv = BuildScriptEnvironment(rootPath, project, action, request.Inputs, profileEnv, resultFilePath,
            request.ProfileName, config.StripEnvVarProfile);

        // Script log file — at root level so it persists regardless of what scripts do
        var logFilePath = GetScriptLogPath(rootPath, projectId);

        // Run prepare scripts (always runs in root directory)
        if (action.Prepare is { Length: > 0 })
        {
            try
            {
                await _scriptRunner.RunAsync(
                    action.Prepare,
                    rootPath,
                    rootPath,
                    scriptEnv,
                    msg => _hubContext.Clients.All.CreationProgress(projectId, msg),
                    logFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Prepare script failed for project {ProjectId}. See log: {LogPath}", projectId, logFilePath);
                project.Status = project.Status with { State = ProjectState.Error };
                _projects[projectId] = project;
                throw;
            }
        }

        // Run create scripts
        if (action.Create is { Length: > 0 })
        {
            // When scripts create the folder, create runs from root (project dir may not exist yet)
            var createWorkDir = action.ScriptsCreateFolder ? rootPath : projectPath;
            try
            {
                await _scriptRunner.RunAsync(
                    action.Create,
                    rootPath,
                    createWorkDir,
                    scriptEnv,
                    msg => _hubContext.Clients.All.CreationProgress(projectId, msg),
                    logFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Create script failed for project {ProjectId}. See log: {LogPath}", projectId, logFilePath);
                project.Status = project.Status with { State = ProjectState.Error };
                _projects[projectId] = project;
                throw;
            }
        }

        // Apply script result overrides (project_path, project_name)
        var scriptResults = ReadResultFile(resultFilePath);
        if (scriptResults.TryGetValue("project_path", out var overridePath) && !string.IsNullOrWhiteSpace(overridePath))
        {
            projectPath = overridePath;
            projectId = Path.GetFileName(overridePath);
            project.ProjectPath = projectPath;
            _logger.LogInformation("Script overrode project path to {ProjectPath} (id: {ProjectId})", projectPath, projectId);
        }
        if (scriptResults.TryGetValue("project_name", out var overrideName) && !string.IsNullOrWhiteSpace(overrideName))
        {
            name = overrideName;
            _logger.LogInformation("Script overrode project name to '{ProjectName}'", name);
        }
        if (scriptResults.TryGetValue("project_prompt", out var overridePrompt) && !string.IsNullOrWhiteSpace(overridePrompt))
        {
            prompt = overridePrompt;
            _logger.LogInformation("Script overrode project prompt ({Length} chars)", prompt.Length);
        }
        project.Status = project.Status with { Id = projectId, Name = name };

        // Ensure .godmode directory exists (scripts may have created the project dir without it)
        EnsureGodModeDirectory(projectPath);

        // Save project settings (persists across restarts, includes action name for delete/resume)
        var skipPermissions = GetBool(request.Inputs, "skipPermissions");
        var settings = new ProjectFiles.ProjectSettings(
            DangerouslySkipPermissions: skipPermissions,
            ActionName: action.Name);
        settings.Save(projectPath);

        // Save initial status
        await _statusUpdater.SaveStatusAsync(project);

        // Add to tracking
        _projects[projectId] = project;

        // Resolve model: user input overrides action config default
        var model = TemplateResolver.GetString(request.Inputs, "model") ?? action.Model;

        // Write MCP config (merges profile + action MCP servers)
        var mcpConfigPath = WriteMcpConfig(projectPath, profileConfig?.McpServers, action.McpServers);

        // Build claude env/args from action config + project settings + profile env
        var (claudeEnv, claudeArgs) = BuildClaudeConfig(action, settings, model, profileEnv,
            request.ProfileName, config.StripEnvVarProfile, mcpConfigPath);

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
            project.Status = project.Status with { State = ProjectState.Error };
            await _statusUpdater.SaveStatusAsync(project);
            throw;
        }

        return project.Status;
    }

    public async Task SendInputAsync(string projectId, string input)
    {
        if (!_projects.TryGetValue(projectId, out var project))
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        await _processManager.SendInputAsync(project, input);

        project.Status = project.Status with
        {
            State = ProjectState.Running,
            CurrentQuestion = null,
            UpdatedAt = DateTime.UtcNow
        };

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

        project.Status = project.Status with
        {
            State = ProjectState.Stopped,
            UpdatedAt = DateTime.UtcNow
        };

        await _statusUpdater.SaveStatusAsync(project);
        await NotifyStatusChanged(project);
    }

    public async Task DeleteProjectAsync(string projectId, bool force = false)
    {
        if (!_projects.TryGetValue(projectId, out var project))
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        _logger.LogInformation("Deleting project {ProjectId} ({Name}), force={Force}", projectId, project.Status.Name, force);

        // Stop Claude process if running
        await _processManager.StopProcessAsync(project);

        // Run delete scripts if configured (failures block deletion)
        // Use rootPath as working directory to avoid Windows CWD lock on project folder
        var snap = _snapshot;
        var profileName = project.ProfileName ?? project.Status.ProfileName;
        if (project.Status.RootName != null && profileName != null)
        {
            var rootPath = snap.ProjectFiles.GetProjectRootPath(CompositeKey(profileName, project.Status.RootName));
            var config = _rootConfigReader.ReadConfig(rootPath);
            var action = config.ResolveAction(project.ActionName);

            if (action?.Delete is { Length: > 0 })
            {
                snap.Profiles.TryGetValue(profileName, out var profileCfg);
                var scriptEnv = BuildScriptEnvironment(rootPath, project, action, new Dictionary<string, JsonElement>(), profileCfg?.Environment,
                    profileName: profileName, stripEnvVarProfile: config.StripEnvVarProfile);

                if (force)
                    scriptEnv["GODMODE_FORCE"] = "true";

                await _scriptRunner.RunAsync(
                    action.Delete,
                    rootPath,
                    rootPath,
                    scriptEnv,
                    msg => _hubContext.Clients.All.CreationProgress(projectId, msg));
            }
        }

        // Remove from tracking
        _projects.TryRemove(projectId, out _);

        // Delete project folder — use robust deletion to handle locked/read-only files
        // (common with .git directories on Windows after git init or process shutdown)
        await DeleteDirectoryRobustAsync(project.ProjectPath);

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
                projectId, project.ProcessId, project.Status.State);

            if (project.Status.State == ProjectState.Idle)
            {
                _logger.LogInformation("Project {ProjectId} is idle with running process, sending continue prompt", projectId);
                await _processManager.SendInputAsync(project, "Continue");
                project.Status = project.Status with { State = ProjectState.Running, UpdatedAt = DateTime.UtcNow };
                await _statusUpdater.SaveStatusAsync(project);
                await NotifyStatusChanged(project);
                return;
            }

            return;
        }

        // Process is not running - check if state needs correction
        if (project.Status.State is ProjectState.Running or ProjectState.WaitingInput)
        {
            _logger.LogWarning("Project {ProjectId} was marked as {State} but process is not running, resetting state",
                projectId, project.Status.State);
            project.Status = project.Status with { State = ProjectState.Stopped };
        }

        if (project.Status.State is not (ProjectState.Stopped or ProjectState.Idle or ProjectState.Error))
        {
            throw new InvalidOperationException($"Project {projectId} cannot be resumed (current state: {project.Status.State})");
        }

        // Stop any existing process/cancellation token
        if (project.ProcessCancellation != null)
        {
            await project.ProcessCancellation.CancelAsync();
            project.ProcessCancellation.Dispose();
        }

        _logger.LogInformation("Resuming project {ProjectId} with session {SessionId}",
            projectId, project.SessionId);

        project.Status = project.Status with { State = ProjectState.Running, UpdatedAt = DateTime.UtcNow };
        project.ProcessCancellation = new CancellationTokenSource();

        // Build claude env/args from action config + persisted project settings + profile env
        Dictionary<string, string>? claudeEnv = null;
        string[]? claudeArgs = null;
        try
        {
            var settings = ProjectFiles.ProjectSettings.Load(project.ProjectPath);
            // Restore action name from settings if not already set (recovery scenario)
            project.ActionName ??= settings.ActionName;

            var resumeSnap = _snapshot;
            var resumeProfileName = project.ProfileName ?? project.Status.ProfileName;
            resumeSnap.Profiles.TryGetValue(resumeProfileName ?? "", out var profileCfg);
            var profileEnv = profileCfg?.Environment;

            if (project.Status.RootName != null && resumeProfileName != null)
            {
                var rootPath = resumeSnap.ProjectFiles.GetProjectRootPath(CompositeKey(resumeProfileName, project.Status.RootName));
                var config = _rootConfigReader.ReadConfig(rootPath);
                var action = config.ResolveAction(project.ActionName);
                if (action != null)
                {
                    var mcpPath = WriteMcpConfig(project.ProjectPath, profileCfg?.McpServers, action.McpServers);
                    (claudeEnv, claudeArgs) = BuildClaudeConfig(action, settings, action.Model, profileEnv,
                        resumeProfileName, config.StripEnvVarProfile, mcpPath);
                }
                else
                    (_, claudeArgs) = BuildClaudeConfig(new CreateAction("Create"), settings,
                        profileEnv: profileEnv, profileName: resumeProfileName,
                        stripEnvVarProfile: config.StripEnvVarProfile);
            }
            else
            {
                // No root config, just apply project settings
                (_, claudeArgs) = BuildClaudeConfig(new CreateAction("Create"), settings, profileEnv: profileEnv);
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
            project.Status = project.Status with { State = ProjectState.Error };
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
        // Rebuild snapshot to include autodiscovered roots before recovery
        if (_projectRootsDir != null)
            RebuildSnapshot();

        var recoverSnap = _snapshot;
        _logger.LogInformation("Recovering projects from all project roots");

        var projectPaths = recoverSnap.ProjectFiles.ListProjectPaths().ToList();

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

                // Determine which profile and root this project belongs to
                string? rootName = null;
                string? profileName = null;
                foreach (var (rn, rp) in recoverSnap.ProjectFiles.ProjectRoots)
                {
                    if (projectPath.StartsWith(rp, StringComparison.OrdinalIgnoreCase))
                    {
                        // Composite key is "profile/root" — split to extract both
                        var slashIdx = rn.IndexOf('/');
                        if (slashIdx > 0)
                        {
                            profileName = rn[..slashIdx];
                            rootName = rn[(slashIdx + 1)..];
                        }
                        else
                        {
                            rootName = rn;
                        }
                        break;
                    }
                }

                var correctedStatus = stateChanged
                    ? status with { State = ProjectState.Stopped, UpdatedAt = DateTime.UtcNow, RootName = rootName, ProfileName = profileName }
                    : status with { RootName = rootName, ProfileName = profileName };

                var project = new ProjectInfo
                {
                    Status = correctedStatus,
                    ProjectPath = projectPath,
                    ProfileName = profileName
                };

                // Load action name from settings
                var settings = ProjectFiles.ProjectSettings.Load(projectPath);
                project.ActionName = settings.ActionName;

                // Load session ID if exists
                var sessionIdPath = Path.Combine(godModePath, "session-id");
                if (File.Exists(sessionIdPath))
                {
                    project.SessionId = await File.ReadAllTextAsync(sessionIdPath, ct);
                }

                _projects[project.Status.Id] = project;

                // Only save if state changed
                if (stateChanged)
                {
                    await _statusUpdater.SaveStatusAsync(project);
                }

                _logger.LogInformation("Recovered project {ProjectId} ({Name})", project.Status.Id, project.Status.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recover project from {Path}", projectPath);
            }
        });
    }

    /// <summary>
    /// Robustly deletes a directory, handling read-only files and retrying on lock conflicts.
    /// Git directories on Windows often have read-only or temporarily locked files.
    /// </summary>
    private async Task DeleteDirectoryRobustAsync(string path)
    {
        if (!Directory.Exists(path)) return;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // Clear read-only attributes that git sets on object files
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    var attrs = File.GetAttributes(file);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (attempt < 2 && ex is UnauthorizedAccessException or IOException)
            {
                _logger.LogWarning("Delete attempt {Attempt} failed for {Path}: {Message}. Retrying...",
                    attempt + 1, path, ex.Message);
                await Task.Delay(500 * (attempt + 1));
            }
        }
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
    /// Uses {rootPath}/logs/{projectId}.log so it survives create script's project dir delete.
    /// </summary>
    private static string GetScriptLogPath(string rootPath, string projectId)
    {
        var logsDir = Path.Combine(rootPath, "logs");
        Directory.CreateDirectory(logsDir);
        return Path.Combine(logsDir, $"{projectId}.log");
    }

    /// <summary>
    /// Returns a result file path for script-to-server communication.
    /// Scripts can write key=value pairs (e.g. project_path, project_name) to override defaults.
    /// </summary>
    private static string GetResultFilePath(string rootPath, string projectId)
    {
        var logsDir = Path.Combine(rootPath, "logs");
        Directory.CreateDirectory(logsDir);
        return Path.Combine(logsDir, $"{projectId}.result");
    }

    /// <summary>
    /// Reads a result file written by scripts. Format: key=value per line. Ignores blank/comment lines.
    /// The last key in the file may span multiple lines (everything after the first '=' until EOF).
    /// This allows scripts to return multiline values (e.g. project_prompt) by placing them last.
    /// Returns empty dictionary if file doesn't exist.
    /// </summary>
    private static Dictionary<string, string> ReadResultFile(string resultFilePath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(resultFilePath)) return result;

        var lines = File.ReadAllLines(resultFilePath);
        string? multilineKey = null;
        List<string>? multilineLines = null;

        foreach (var line in lines)
        {
            if (multilineKey != null)
            {
                // We're accumulating lines for the last key
                multilineLines!.Add(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var eqIndex = line.IndexOf('=');
            if (eqIndex <= 0) continue;
            var key = line[..eqIndex].Trim();
            var value = line[(eqIndex + 1)..];
            result[key] = value.Trim();
        }

        // Find the last key and re-read its value as multiline (everything after key= to EOF)
        // This lets scripts place a multiline value (like a prompt) as the last entry.
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]) || lines[i].StartsWith('#')) continue;
            var eqIndex = lines[i].IndexOf('=');
            if (eqIndex <= 0) continue;

            var lastKey = lines[i][..eqIndex].Trim();
            // Collect all lines from this key's value line to end of file
            var firstValueLine = lines[i][(eqIndex + 1)..];
            var valueParts = new List<string> { firstValueLine };
            for (int j = i + 1; j < lines.Length; j++)
                valueParts.Add(lines[j]);

            var multiValue = string.Join(Environment.NewLine, valueParts).Trim();
            if (!string.IsNullOrEmpty(multiValue))
                result[lastKey] = multiValue;
            break;
        }

        return result;
    }

    /// <summary>
    /// Resolves the project name from inputs or nameTemplate.
    /// </summary>
    private static string? ResolveProjectName(CreateAction action, Dictionary<string, JsonElement> inputs)
    {
        if (action.NameTemplate != null)
            return TemplateResolver.Resolve(action.NameTemplate, inputs);

        return TemplateResolver.GetString(inputs, "name");
    }

    /// <summary>
    /// Resolves the initial prompt from inputs or promptTemplate.
    /// </summary>
    private static string? ResolvePrompt(CreateAction action, Dictionary<string, JsonElement> inputs)
    {
        if (action.PromptTemplate != null)
            return TemplateResolver.Resolve(action.PromptTemplate, inputs);

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
    /// Merges environment from all layers and applies ${VAR} expansion + optional prefix stripping.
    /// Merge order: profile env (stripped vars + explicit config) → action env, then ${VAR} expansion.
    /// </summary>
    private static Dictionary<string, string>? MergeAndExpandEnvironment(
        Dictionary<string, string>? profileEnv,
        Dictionary<string, string>? actionEnv,
        string? profileName = null,
        bool stripEnvVarProfile = false)
    {
        Dictionary<string, string>? env = null;

        // Profile environment: prefix-stripped vars + explicit config (same priority layer)
        if (EnvironmentExpander.IsStripEnabled(profileName, stripEnvVarProfile))
        {
            var stripped = EnvironmentExpander.GetPrefixStrippedVars(profileName);
            if (stripped is { Count: > 0 })
                env = new Dictionary<string, string>(stripped);
        }

        // Explicit profile env merges on top of stripped vars (both are "profile" layer)
        if (profileEnv is { Count: > 0 })
        {
            env ??= new Dictionary<string, string>();
            foreach (var (key, value) in profileEnv)
                env[key] = value;
        }

        // Action environment overrides profile
        if (actionEnv is { Count: > 0 })
        {
            env ??= new Dictionary<string, string>();
            foreach (var (key, value) in actionEnv)
                env[key] = value;
        }

        // Expand ${VAR} references — entries with unresolvable vars are removed
        return EnvironmentExpander.ExpandVariables(env);
    }

    /// <summary>
    /// Builds claude environment and args from action config + project settings + profile env.
    /// </summary>
    private static (Dictionary<string, string>? Env, string[]? Args) BuildClaudeConfig(
        CreateAction action, ProjectFiles.ProjectSettings settings,
        string? model = null,
        Dictionary<string, string>? profileEnv = null,
        string? profileName = null,
        bool stripEnvVarProfile = false,
        string? mcpConfigPath = null)
    {
        var env = MergeAndExpandEnvironment(profileEnv, action.Environment, profileName, stripEnvVarProfile);

        var args = new List<string>();
        if (action.ClaudeArgs != null)
            args.AddRange(action.ClaudeArgs);
        if (settings.DangerouslySkipPermissions)
            args.Add("--dangerously-skip-permissions");
        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("--model");
            args.Add(model);
        }
        if (!string.IsNullOrWhiteSpace(mcpConfigPath))
        {
            args.Add("--mcp-config");
            args.Add(mcpConfigPath);
        }

        return (env, args.Count > 0 ? args.ToArray() : null);
    }

    /// <summary>
    /// Merges MCP servers from profile and action levels and writes .godmode/mcp-config.json.
    /// Returns the file path if MCP servers were written, null otherwise.
    /// Merge order: profile → action (action wins on conflict).
    /// Expands ${VAR} references in env values.
    /// </summary>
    private static string? WriteMcpConfig(
        string projectPath,
        Dictionary<string, McpServerConfig>? profileMcpServers,
        Dictionary<string, McpServerConfig>? actionMcpServers)
    {
        // Merge: profile is the base, action overrides
        Dictionary<string, McpServerConfig>? merged = null;
        if (profileMcpServers != null || actionMcpServers != null)
        {
            merged = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);
            if (profileMcpServers != null)
                foreach (var (k, v) in profileMcpServers)
                    merged[k] = v;
            if (actionMcpServers != null)
                foreach (var (k, v) in actionMcpServers)
                    merged[k] = v;
        }

        if (merged is not { Count: > 0 })
            return null;

        // Build Claude MCP config format: { "mcpServers": { ... } }
        var mcpConfig = new Dictionary<string, object>
        {
            ["mcpServers"] = merged.ToDictionary(
                kvp => kvp.Key,
                kvp => new
                {
                    command = kvp.Value.Command,
                    args = kvp.Value.Args ?? [],
                    env = ExpandEnvVars(kvp.Value.Env)
                })
        };

        var configPath = Path.Combine(projectPath, ".godmode", "mcp-config.json");
        var json = JsonSerializer.Serialize(mcpConfig, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configPath, json);

        return configPath;
    }

    /// <summary>
    /// Expands ${VAR} references in dictionary values using the current process environment.
    /// </summary>
    private static Dictionary<string, string>? ExpandEnvVars(Dictionary<string, string>? env)
    {
        if (env is null or { Count: 0 }) return env;

        var expanded = new Dictionary<string, string>(env.Count);
        foreach (var (key, value) in env)
        {
            expanded[key] = System.Text.RegularExpressions.Regex.Replace(
                value, @"\$\{(\w+)\}",
                m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);
        }
        return expanded;
    }

    /// <summary>
    /// Builds the full environment variables dictionary for scripts.
    /// Merge order: prefix-stripped vars (auto) → profile env → action env → ${VAR} expansion → GODMODE_* vars.
    /// </summary>
    private static Dictionary<string, string> BuildScriptEnvironment(
        string rootPath,
        ProjectInfo project,
        CreateAction action,
        Dictionary<string, JsonElement> inputs,
        Dictionary<string, string>? profileEnv = null,
        string? resultFilePath = null,
        string? profileName = null,
        bool stripEnvVarProfile = false)
    {
        var env = MergeAndExpandEnvironment(profileEnv, action.Environment, profileName, stripEnvVarProfile)
            ?? new Dictionary<string, string>();

        // GODMODE_* vars always win
        env["GODMODE_ROOT_PATH"] = rootPath;
        env["GODMODE_PROJECT_PATH"] = project.ProjectPath;
        env["GODMODE_PROJECT_ID"] = project.Status.Id;
        env["GODMODE_PROJECT_NAME"] = project.Status.Name;

        if (resultFilePath != null)
            env["GODMODE_RESULT_FILE"] = resultFilePath;

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

        var id = project.Status.Id;

        _logger.LogInformation("HandleOutputReceivedAsync for project {ProjectId}: {Line}",
            id, jsonLine.Length > 100 ? jsonLine[..100] + "..." : jsonLine);

        try
        {
            // Extract type for logging and status updates
            var eventType = ExtractEventType(jsonLine);

            _logger.LogInformation("Sending raw JSON to group 'project-{ProjectId}', Type: {Type}",
                id, eventType);

            // Send raw JSON to subscribed clients - UI will parse and render
            await _hubContext.Clients.Group($"project-{id}")
                .OutputReceived(id, jsonLine);

            _logger.LogInformation("OutputReceived sent successfully for project {ProjectId}", id);

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
            _logger.LogError(ex, "Error in HandleOutputReceivedAsync for project {ProjectId}", id);
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
        var id = project.Status.Id;
        var outputPath = Path.Combine(project.ProjectPath, ".godmode", "output.jsonl");

        _logger.LogInformation("SendOutputFromOffsetAsync called for project {ProjectId}, offset: {Offset}, connectionId: {ConnectionId}",
            id, offset, connectionId);

        if (!File.Exists(outputPath))
        {
            _logger.LogInformation("No output file exists yet for project {ProjectId}", id);
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
                    lineCount, connectionId, id, eventType);

                // Send raw JSON to client - UI will parse and render
                await _hubContext.Clients.Client(connectionId)
                    .OutputReceived(id, line);
            }

            _logger.LogInformation("Sent {LineCount} existing output lines to client {ConnectionId} for project {ProjectId}",
                lineCount, connectionId, id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending output from offset for project {ProjectId}", id);
        }
    }

    private async Task NotifyStatusChanged(ProjectInfo project)
    {
        await _hubContext.Clients.All.StatusChanged(project.Status.Id, project.Status);
    }

    private string GenerateMetricsHtml(ProjectInfo project)
    {
        var s = project.Status;
        return $@"
<!DOCTYPE html>
<html>
<head>
    <title>Metrics - {s.Name}</title>
    <style>
        body {{ font-family: Arial, sans-serif; margin: 20px; }}
        .metric {{ margin: 10px 0; }}
        .label {{ font-weight: bold; }}
    </style>
</head>
<body>
    <h1>Project Metrics: {s.Name}</h1>
    <div class=""metric"">
        <span class=""label"">Input Tokens:</span> {s.Metrics.InputTokens:N0}
    </div>
    <div class=""metric"">
        <span class=""label"">Output Tokens:</span> {s.Metrics.OutputTokens:N0}
    </div>
    <div class=""metric"">
        <span class=""label"">Tool Calls:</span> {s.Metrics.ToolCalls}
    </div>
    <div class=""metric"">
        <span class=""label"">Duration:</span> {s.Metrics.Duration}
    </div>
    <div class=""metric"">
        <span class=""label"">Cost Estimate:</span> ${s.Metrics.CostEstimate:F4}
    </div>
</body>
</html>";
    }
}
