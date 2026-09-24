using GodMode.Server.Models;
using GodMode.Shared;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
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
public class ProjectManager : IProjectManager, IAsyncDisposable, IDisposable
{
    /// <summary>How long server shutdown waits for the projects' processes to be killed and marked Stopped.</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The name the GodMode MCP bridge has in every session's MCP config.</summary>
    internal const string McpBridgeServerName = "godmode-bridge";

    /// <summary>
    /// The bridge tool claude asks for permission with (--permission-prompt-tool), and puts its
    /// AskUserQuestion calls to: see <see cref="RequestPermissionAsync"/>.
    /// </summary>
    internal const string PermissionPromptTool = $"mcp__{McpBridgeServerName}__permission_prompt";

    /// <summary>The setting that points at the MCP bridge bundle, overriding where the build puts it.</summary>
    public const string McpBridgePathSetting = "McpBridgePath";

    /// <summary>Where the build and a publish put the bridge bundle, relative to the server's binaries.</summary>
    private static readonly string BundledMcpBridge = Path.Combine("mcp-bridge", "godmode-mcp-bridge.cjs");

    private readonly ProjectLifecycle _lifecycle;
    private readonly IStatusUpdater _statusUpdater;
    private readonly IRootConfigReader _rootConfigReader;
    private readonly IScriptRunner _scriptRunner;
    private readonly IHubContext<ProjectHub, IProjectHubClient> _hubContext;
    private readonly ProfileFileManager _profileFileManager;
    private readonly ILogger<ProjectManager> _logger;
    private readonly ConcurrentDictionary<string, ProjectInfo> _projects = new();
    private readonly string _mcpBridgePath;
    private readonly IServer? _server;
    private readonly string[] _configuredUrls;

    /// <summary>How long a reply that resumes a project waits for claude to report its session started.</summary>
    public const string SessionStartTimeoutSetting = "SessionStartTimeoutSeconds";
    private readonly TimeSpan _sessionStartTimeout;

    /// <summary>The attention list last pushed, and the lock that orders computing and pushing it.</summary>
    private AttentionItem[] _attention = [];
    private readonly SemaphoreSlim _attentionLock = new(1, 1);

    /// <summary>How often an open pull request is checked, and how long its root's status script may take.</summary>
    public const string PullRequestPollSetting = "PullRequestPollSeconds";
    public const string StatusScriptTimeoutSetting = "StatusScriptTimeoutSeconds";
    private readonly TimeSpan _statusScriptTimeout;
    private readonly PullRequestPoller _pullRequests;

    /// <inheritdoc />
    public event Func<string, Task>? OnProjectCompleted
    {
        add => _lifecycle.OnProjectCompleted += value;
        remove => _lifecycle.OnProjectCompleted -= value;
    }

    /// <summary>
    /// Legacy profiles loaded from appsettings.json at startup (before .profiles/ migration).
    /// </summary>
    private readonly Dictionary<string, ProfileConfig> _legacyProfiles;

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
        ProjectLifecycle lifecycle,
        IStatusUpdater statusUpdater,
        IRootConfigReader rootConfigReader,
        IScriptRunner scriptRunner,
        IHubContext<ProjectHub, IProjectHubClient> hubContext,
        ProfileFileManager profileFileManager,
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        ILogger<ProjectManager> logger,
        IServer? server = null)
    {
        _lifecycle = lifecycle;
        _statusUpdater = statusUpdater;
        _rootConfigReader = rootConfigReader;
        _scriptRunner = scriptRunner;
        _hubContext = hubContext;
        _profileFileManager = profileFileManager;
        _logger = logger;
        _mcpBridgePath = ResolveMcpBridgePath(configuration);
        _server = server;
        _configuredUrls = (configuration["Urls"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _sessionStartTimeout = TimeSpan.FromSeconds(configuration.GetValue(SessionStartTimeoutSetting, 60.0));
        _statusScriptTimeout = TimeSpan.FromSeconds(configuration.GetValue(StatusScriptTimeoutSetting, 30.0));
        _pullRequests = new PullRequestPoller(CheckPullRequestAsync,
            TimeSpan.FromSeconds(configuration.GetValue(PullRequestPollSetting, 600.0)), logger);
        _lifecycle.StatusNotified += OnStatusNotifiedAsync;

        // Read optional autodiscovery directory (normalize empty/whitespace to null)
        var rawDir = configuration["ProjectRootsDir"];
        _projectRootsDir = string.IsNullOrWhiteSpace(rawDir) ? null : rawDir;

        // Migrate legacy profiles from appsettings.json to .profiles/ (one-time)
        _profileFileManager.MigrateFromAppSettings(configuration);

        // Load legacy profiles from configuration (with backward compat for ProjectRoots)
        _legacyProfiles = LoadProfiles(configuration, hasAutoDiscovery: _projectRootsDir != null);

        if (_projectRootsDir != null)
            _logger.LogInformation("Autodiscovery enabled: scanning {ProjectRootsDir} for .godmode-root/ directories", _projectRootsDir);

        // Build initial profile/root snapshot
        _snapshot = BuildSnapshot();

        lifetime.ApplicationStopping.Register(StopProjectsOnShutdown);
    }

    /// <summary>
    /// Server shutdown: kills every claude process tree and persists Stopped, so recovery on the
    /// next start does not launch a second process on a session an orphan still runs. A Ctrl+C
    /// on a server run in a terminal reaches claude too, which may already have exited: from here
    /// on its exit counts as stopped, and its project is stopped like the rest, so the exit is
    /// persisted before the server goes. Blocks shutdown until done or <see cref="ShutdownTimeout"/> passes.
    /// </summary>
    private void StopProjectsOnShutdown()
    {
        // Before the projects stop: going Stopped starts no status script now
        _pullRequests.Stop();
        _lifecycle.BeginShutdown();
        var running = _projects.Values
            .Where(project => project.Process.ProcessId != 0
                || project.Status.State is ProjectState.Running or ProjectState.WaitingInput or ProjectState.WaitingPermission or ProjectState.Idle)
            .ToArray();
        if (running.Length == 0) return;

        _logger.LogInformation("Server stopping: stopping {Count} running project(s)", running.Length);
        var stops = Task.WhenAll(running.Select(async project =>
        {
            try
            {
                await _lifecycle.StopAsync(project);
                await NotifyStatusChanged(project);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not stop project {ProjectId} on shutdown", project.Status.Id);
            }
        }));
        if (!stops.Wait(ShutdownTimeout))
            _logger.LogWarning("Server stopping: projects were not all stopped within {Timeout}", ShutdownTimeout);
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
        // Layer 1: Legacy profiles from appsettings.json (only if .profiles/ doesn't exist yet)
        // Once .profiles/ exists (migration has run), legacy is ignored — .profiles/ is authoritative.
        var profilesDirExists = Directory.Exists(_profileFileManager.ProfilesDir);
        var merged = profilesDirExists
            ? new Dictionary<string, ProfileConfig>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ProfileConfig>(_legacyProfiles, StringComparer.OrdinalIgnoreCase);

        // Layer 2: File-based profiles from .profiles/ directory
        var fileProfiles = _profileFileManager.ReadAllProfiles();
        foreach (var (name, data) in fileProfiles)
        {
            if (merged.TryGetValue(name, out var existing))
            {
                // File-based profile fully replaces legacy environment/MCP/description.
                // No fallback to legacy — once .profiles/{name}/ exists, it is authoritative.
                merged[name] = new ProfileConfig
                {
                    Roots = existing.Roots,
                    Environment = data.Environment,
                    Description = data.Description ?? existing.Description,
                    McpServers = data.McpServers
                };
            }
            else
            {
                merged[name] = new ProfileConfig
                {
                    Roots = new Dictionary<string, string>(),
                    Environment = data.Environment,
                    Description = data.Description,
                    McpServers = data.McpServers
                };
            }
        }

        // Layer 3: Autodiscovered roots from ProjectRootsDir
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
                        Description = existing.Description ?? profileConfig.Description,
                        McpServers = existing.McpServers
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
                var profileName = config.ProfileName ?? "Default";

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

    /// <summary>
    /// A project's ID: <c>{profile}/{root}/{folder}</c>, where it lives, so a folder name used in two
    /// roots is two projects. Clients treat it as opaque; it is never parsed. It is derived again
    /// from the folder's location on every recovery, not trusted from status.json.
    /// </summary>
    private static string ProjectId(string profile, string root, string folder) => $"{CompositeKey(profile, root)}/{folder}";

    /// <summary>Every root with its profile and root names as configured, and its full path.</summary>
    private static IEnumerable<(string Profile, string Root, string Path)> AllRoots(ProfileSnapshot snap) =>
        snap.RootLookup.Keys.Select(key => (key.Item1, key.Item2, snap.ProjectFiles.GetProjectRootPath(CompositeKey(key.Item1, key.Item2))));

    /// <summary>The profile and root names as configured, for names a client may have cased differently.</summary>
    private static (string Profile, string Root) ConfiguredNames(ProfileSnapshot snap, string profile, string root) =>
        snap.RootLookup.Keys.FirstOrDefault(key => TupleComparer.Instance.Equals(key, (profile, root))) is ({ } p, { } r) ? (p, r) : (profile, root);

    /// <summary>Whether <paramref name="path"/> is <paramref name="dir"/> or inside it, by whole path segments.</summary>
    private static bool IsSameOrUnder(string path, string dir)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(dir), Path.GetFullPath(path));
        return relative == "."
            || !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar);
    }

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
                ProfileName: project.ProfileName ?? s.ProfileName,
                PendingPermission: s.PendingPermission,
                PendingQuestion: s.PendingQuestion,
                PullRequest: s.PullRequest
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
        // A create that fails leaves an Error project behind, which needs the user
        try { return await CreateProjectCoreAsync(request); }
        finally { await PushAttentionIfChangedAsync(); }
    }

    private async Task<ProjectStatus> CreateProjectCoreAsync(CreateProjectRequest request)
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

        // Create project folder — either server-managed or script-managed. A name that leaves no
        // folder of its own ("..", ".") is refused here, before any script runs or file is written
        var folder = ProjectFiles.ProjectManager.ConvertNameToPath(name);
        var reuseExisting = request.Inputs.TryGetValue("__reuseExisting", out var reuse) &&
                            reuse.ValueKind == System.Text.Json.JsonValueKind.True;
        var autoSuffix = request.Inputs.TryGetValue("__autoSuffix", out var suffix) &&
                         suffix.ValueKind == System.Text.Json.JsonValueKind.True;
        string projectPath;

        if (action.ScriptsCreateFolder)
        {
            // Scripts will create the project directory (e.g. git worktree add)
            projectPath = Path.Combine(rootPath, folder);
        }
        else if (reuseExisting)
        {
            // Reuse existing folder — reinitialize .godmode state
            var projectFolder = ProjectFiles.ProjectFolder.Reuse(rootPath, folder, name);
            projectPath = projectFolder.ProjectPath;
            folder = Path.GetFileName(projectPath);
        }
        else if (autoSuffix && Directory.Exists(Path.Combine(rootPath, folder)))
        {
            // Auto-suffix: find next available _N
            var baseFolder = folder;
            var baseName = name;
            for (var i = 2; i <= 999; i++)
            {
                var candidate = $"{baseFolder}_{i}";
                if (!Directory.Exists(Path.Combine(rootPath, candidate)))
                {
                    folder = candidate;
                    name = $"{baseName} ({i})";
                    break;
                }
            }
            var suffixedFolder = ProjectFiles.ProjectFolder.Create(rootPath, folder, name);
            projectPath = suffixedFolder.ProjectPath;
        }
        else
        {
            // Server creates the project folder via ProjectFiles
            var (projectFolder, _) = snap.ProjectFiles.CreateProject(compositeKey, name);
            projectPath = projectFolder.ProjectPath;
        }

        var (profileName, rootName) = ConfiguredNames(snap, request.ProfileName, request.ProjectRootName);
        var projectId = ProjectId(profileName, rootName, folder);

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
                RootName: rootName,
                ProfileName: profileName
            ),
            ProjectPath = projectPath,
            ActionName = action.Name,
            ProfileName = profileName,
        };

        // Result file — scripts can write key=value pairs to override project path/name
        var resultFilePath = GetResultFilePath(rootPath, folder);
        if (File.Exists(resultFilePath)) File.Delete(resultFilePath);

        // Build environment variables for scripts (profile env merged in)
        var scriptEnv = BuildScriptEnvironment(rootPath, project, action, request.Inputs, profileEnv, resultFilePath,
            request.ProfileName, config.StripEnvVarProfile);

        // Script log file — at root level so it persists regardless of what scripts do
        var logFilePath = GetScriptLogPath(rootPath, folder);

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
            try
            {
                (projectPath, folder) = ValidateScriptProjectPath(overridePath, rootPath);
            }
            catch (ArgumentException ex)
            {
                // The project keeps the folder it was given, so a delete removes only that
                _logger.LogError("Create script for project {ProjectId} returned an invalid project_path: {Message}", projectId, ex.Message);
                project.Status = project.Status with { State = ProjectState.Error };
                _projects[projectId] = project;
                throw;
            }
            projectId = ProjectId(profileName, rootName, folder);
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
        // Resolve model: user input overrides action config default.
        // Persisted in status.json so resumes keep using the same model even if the
        // root config changes or the machine-wide Claude default differs.
        var model = TemplateResolver.GetString(request.Inputs, "model") ?? action.Model;
        project.Status = project.Status with { Id = projectId, Name = name, Model = model };

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

        // Start Claude process, configured from what is saved above, exactly as a resume will be
        try
        {
            await _lifecycle.StartAsync(project, prompt ?? "Hello", BuildLaunchSpec(project));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Claude process for project {ProjectId}", projectId);
            await _lifecycle.UpdateStatusAsync(project, status => status with { State = ProjectState.Error });
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

        // claude is blocked on a permission prompt and reads no input until it is answered: a reply
        // in the chat answers it. A single question takes it as its answer; anything else is a deny
        // that tells claude what the user said instead
        if (project.Process.OldestPending is { } pending)
        {
            var result = pending.Question is { Questions: [var only] }
                ? PermissionPromptResult.Allow(PermissionPrompts.WithAnswers(pending.Input, new Dictionary<string, string> { [only.Question] = input }))
                : PermissionPromptResult.Deny($"The user did not answer this and wrote instead: {input}");
            await CompletePendingAsync(project, pending, result);
            return;
        }

        await _lifecycle.SendInputAsync(project, input);
        await NotifyStatusChanged(project);
    }

    public async Task ReplyAndResumeAsync(string projectId, string text)
    {
        if (!_projects.TryGetValue(projectId, out var project))
            throw new KeyNotFoundException($"Project {projectId} not found");

        // One reply at a time decides whether to resume: two would launch two processes
        var resumeLock = project.Process.ResumeLock;
        await resumeLock.WaitAsync();
        try
        {
            if (_lifecycle.IsRunning(project))
            {
                await SendInputAsync(projectId, text);
                return;
            }

            // claude writes system/init once it has read its first input, so the reply is sent at
            // once and the session start awaited after it
            var sessionStart = project.Process.NextSessionStart();
            await ResumeProjectAsync(projectId);
            var sentTo = await TrySendInputAsync(project, text);
            await NotifyStatusChanged(project);

            int startedIn;
            try
            {
                startedIn = await sessionStart.WaitAsync(_sessionStartTimeout);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"Project {projectId} was resumed, but claude did not start its session within {_sessionStartTimeout.TotalSeconds:0} seconds");
            }

            // The resume found no conversation and a fresh session took its place: the reply went
            // to the process that gave up, or reached none
            if (startedIn != sentTo)
            {
                _logger.LogInformation("Project {ProjectId}: its session started in another process than the reply went to; sending it again", projectId);
                await _lifecycle.SendInputAsync(project, text);
                await NotifyStatusChanged(project);
            }
        }
        finally
        {
            resumeLock.Release();
        }
    }

    /// <summary>Sends to the process just launched; 0 when it has exited already (a fresh session may take its place).</summary>
    private async Task<int> TrySendInputAsync(ProjectInfo project, string text)
    {
        try { return await _lifecycle.SendInputAsync(project, text); }
        catch (InvalidOperationException ex)
        {
            _logger.LogInformation("Project {ProjectId}: the resumed process took no input ({Message})", project.Status.Id, ex.Message);
            return 0;
        }
    }

    public AttentionItem[] GetAttention() => Attention.Of(_projects.Values.Select(project => project.Status));

    public async Task MarkSeenAsync(string projectId)
    {
        if (!_projects.TryGetValue(projectId, out var project))
            throw new KeyNotFoundException($"Project {projectId} not found");

        // Not UpdatedAt: it dates an Error, and seeing a result changes no state
        await _lifecycle.UpdateStatusAsync(project, status => status with { SeenAt = DateTime.UtcNow });
        await NotifyStatusChanged(project);
    }

    /// <summary>After every status push: the pull request check a transition to Idle or Stopped makes, then the attention list.</summary>
    private Task OnStatusNotifiedAsync(ProjectInfo project)
    {
        _pullRequests.Observe(project.Status.Id, project.Status.State);
        return PushAttentionIfChangedAsync();
    }

    /// <summary>
    /// Runs the project's root's status script in its folder, and keeps the pull request it reports in
    /// the status, pushing it when it changed. A root without one, a script that fails, times out or
    /// prints anything but the documented JSON (<see cref="PullRequestScript"/>) changes nothing; the
    /// failure is logged. Says whether to poll: while the pull request it knows is open.
    /// </summary>
    private async Task<PullRequestPoller.Outcome> CheckPullRequestAsync(string projectId, CancellationToken cancel)
    {
        if (!_projects.TryGetValue(projectId, out var project)) return PullRequestPoller.Outcome.Gone;
        var unchanged = project.Status.PullRequest is { IsOpen: true } ? PullRequestPoller.Outcome.Poll : PullRequestPoller.Outcome.Wait;
        var profileName = project.ProfileName ?? project.Status.ProfileName;
        if (project.Status.RootName == null || profileName == null) return unchanged;

        string? script = null;
        PullRequestStatus? reported;
        try
        {
            var snap = _snapshot;
            var rootPath = snap.ProjectFiles.GetProjectRootPath(CompositeKey(profileName, project.Status.RootName));
            var config = _rootConfigReader.ReadConfig(rootPath);
            if (config.ResolveAction(project.ActionName) is not { Status: { } status } action) return unchanged;
            script = status;

            snap.Profiles.TryGetValue(profileName, out var profileCfg);
            var env = BuildScriptEnvironment(rootPath, project, action, new Dictionary<string, JsonElement>(), profileCfg?.Environment,
                profileName: profileName, stripEnvVarProfile: config.StripEnvVarProfile);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(_statusScriptTimeout);
            var output = await _scriptRunner.RunForOutputAsync(script, rootPath, project.ProjectPath, env, PullRequestScript.MaxOutputChars, timeout.Token);
            reported = PullRequestScript.Parse(output, DateTime.UtcNow);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Project {ProjectId}: status script {Script} failed, so its pull request is left as it was: {Reason}",
                projectId, script, ex is OperationCanceledException ? $"it took longer than {_statusScriptTimeout.TotalSeconds}s" : ex.Message);
            return unchanged;
        }

        cancel.ThrowIfCancellationRequested();
        if (!_projects.TryGetValue(projectId, out var current) || current != project) return PullRequestPoller.Outcome.Gone;
        var before = project.Status.PullRequest;
        var after = PullRequestScript.Apply(before, reported);
        if (after != before)
        {
            _logger.LogInformation("Project {ProjectId}: pull request {PullRequest}", projectId,
                after is { } pr ? $"#{pr.Number} is {pr.State}, review {pr.Review}" : "none");
            await _lifecycle.UpdateStatusAsync(project, status => status with { PullRequest = after });
            await NotifyStatusChanged(project);
        }
        return after is { IsOpen: true } ? PullRequestPoller.Outcome.Poll : PullRequestPoller.Outcome.Wait;
    }

    /// <summary>A project that is back without a status push (recovered, restored): its open pull request is checked, then polled.</summary>
    private void ResumeChecks(ProjectInfo project)
    {
        _pullRequests.Remember(project.Status.Id, project.Status.State);
        if (project.Status.PullRequest is { IsOpen: true }) _pullRequests.CheckNow(project.Status.Id);
    }

    public async ValueTask DisposeAsync() => await _pullRequests.DisposeAsync();

    public void Dispose() => _pullRequests.Dispose();

    /// <summary>Pushes the attention list to every client when it differs from the one last pushed.</summary>
    private async Task PushAttentionIfChangedAsync()
    {
        await _attentionLock.WaitAsync();
        try
        {
            var attention = GetAttention();
            if (Attention.Same(attention, _attention)) return;
            _attention = attention;
            await _hubContext.Clients.All.AttentionChanged(attention);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pushing the attention list");
        }
        finally
        {
            _attentionLock.Release();
        }
    }

    public async Task RespondToPermissionAsync(string projectId, string requestId, PermissionDecision decision)
    {
        var (project, pending) = FindPending(projectId, requestId);
        if (decision.Allow && pending.Question != null)
            throw new InvalidOperationException($"Request {requestId} is a question: answer it with AnswerQuestion");

        var result = decision.Allow
            ? PermissionPromptResult.Allow(decision.UpdatedInput ?? pending.Input)
            : PermissionPromptResult.Deny(decision.Message is { Length: > 0 } message ? message : "The user denied this.");
        _logger.LogInformation("Project {ProjectId}: permission request {RequestId} {Decision}",
            projectId, requestId, decision.Allow ? "allowed" : "denied");
        await CompletePendingAsync(project, pending, result);
    }

    public async Task AnswerQuestionAsync(string projectId, string requestId, IReadOnlyDictionary<string, string> answers)
    {
        var (project, pending) = FindPending(projectId, requestId);
        if (pending.Question == null)
            throw new InvalidOperationException($"Request {requestId} is not a question: answer it with RespondToPermission");
        if (answers.Count == 0)
            throw new ArgumentException("An answer needs at least one question answered", nameof(answers));

        _logger.LogInformation("Project {ProjectId}: question {RequestId} answered", projectId, requestId);
        await CompletePendingAsync(project, pending, PermissionPromptResult.Allow(PermissionPrompts.WithAnswers(pending.Input, answers)));
    }

    public async Task<PermissionPromptResult> RequestPermissionAsync(string projectId, PermissionPromptRequest request, CancellationToken aborted)
    {
        if (!_projects.TryGetValue(projectId, out var project))
            throw new KeyNotFoundException($"Project {projectId} not found");

        var pending = PermissionPrompts.Create(request, project.ProjectPath, DateTime.UtcNow);
        // Tool name only: the input and its summary can carry secrets (a command with a token in it)
        _logger.LogInformation("Project {ProjectId} asks permission for {ToolName} (request {RequestId})",
            projectId, request.ToolName, pending.Id);
        project.Process.AddPending(pending);
        await _lifecycle.ShowPendingAsync(project);

        try
        {
            return await pending.Completion.Task.WaitAsync(aborted);
        }
        catch (OperationCanceledException)
        {
            // The bridge went away (claude exited or was killed): nobody is waiting for the answer
            _logger.LogInformation("Project {ProjectId}: permission request {RequestId} was abandoned", projectId, pending.Id);
            await CompletePendingAsync(project, pending, PermissionPromptResult.Deny("The request was abandoned."));
            throw;
        }
    }

    private (ProjectInfo Project, PendingRequest Pending) FindPending(string projectId, string requestId)
    {
        if (!_projects.TryGetValue(projectId, out var project))
            throw new KeyNotFoundException($"Project {projectId} not found");
        return project.Process.FindPending(requestId) is { } pending
            ? (project, pending)
            : throw new KeyNotFoundException($"Project {projectId} has no pending request {requestId}: it was answered, or claude stopped waiting");
    }

    /// <summary>Answers the request, if nothing else did first, and shows the next one or none.</summary>
    private async Task CompletePendingAsync(ProjectInfo project, PendingRequest pending, PermissionPromptResult result)
    {
        if (project.Process.CompletePending(pending, result))
            await _lifecycle.ShowPendingAsync(project);
    }

    public async Task StopProjectAsync(string projectId)
    {
        if (!_projects.TryGetValue(projectId, out var project))
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        await _lifecycle.StopAsync(project);
        await NotifyStatusChanged(project);
    }

    public async Task DeleteProjectAsync(string projectId, bool force = false)
    {
        if (!_projects.TryGetValue(projectId, out var project))
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        _logger.LogInformation("Deleting project {ProjectId} ({Name}), force={Force}", projectId, project.Status.Name, force);

        // Stop Claude process if running. Its output pipeline stays open until the delete is
        // committed: a delete script may refuse, and the project is then resumed as before
        await _lifecycle.KillAsync(project);
        // Nor does a status script hold its folder while the delete scripts run
        await _pullRequests.ForgetAsync(projectId);

        // Run delete scripts if configured (failures block deletion)
        // Use rootPath as working directory to avoid Windows CWD lock on project folder
        var snap = _snapshot;
        var profileName = project.ProfileName ?? project.Status.ProfileName;
        try
        {
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
        }
        catch
        {
            // Refused: the project stays, and so do its checks
            ResumeChecks(project);
            throw;
        }

        // Remove from tracking, and finish its output and any check a push started since
        _projects.TryRemove(projectId, out _);
        await project.Process.CloseAsync();
        await _pullRequests.ForgetAsync(projectId);

        // Delete project folder — use robust deletion to handle locked/read-only files
        // (common with .git directories on Windows after git init or process shutdown)
        await DeleteDirectoryRobustAsync(project.ProjectPath);

        _logger.LogInformation("Project {ProjectId} deleted successfully", projectId);
        await PushAttentionIfChangedAsync();
    }

    public async Task ArchiveProjectAsync(string projectId)
    {
        if (!_projects.TryRemove(projectId, out var project))
            throw new KeyNotFoundException($"Project {projectId} not found");

        // Stop process if running, and finish its output and its checks
        await _lifecycle.CloseAsync(project);
        await _pullRequests.ForgetAsync(projectId);

        // Move project folder to .archived/ sibling directory
        var parentDir = Path.GetDirectoryName(project.ProjectPath)!;
        var archiveDir = Path.Combine(parentDir, ".archived");
        Directory.CreateDirectory(archiveDir);
        var destDir = Path.Combine(archiveDir, Path.GetFileName(project.ProjectPath));
        if (Directory.Exists(destDir))
            await DeleteDirectoryRobustAsync(destDir);
        Directory.Move(project.ProjectPath, destDir);

        // Write archive metadata
        var metaPath = Path.Combine(destDir, ".godmode", "archive.json");
        var meta = new { ArchivedAt = DateTime.UtcNow, Name = project.Status.Name,
            RootName = project.Status.RootName, ProfileName = project.ProfileName ?? project.Status.ProfileName };
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));

        _logger.LogInformation("Archived project {ProjectId} ({Name})", projectId, project.Status.Name);
        await PushAttentionIfChangedAsync();
    }

    public Task<ProjectSummary[]> ListArchivedProjectsAsync()
    {
        var results = new List<ProjectSummary>();
        var snap = _snapshot;

        // Scan all root directories for .archived/ folders. An archived project's ID is where it
        // returns to, its root and folder, whatever ID its status.json was archived with
        foreach (var (profileName, rootName, rootPath) in AllRoots(snap))
        {
            var archiveDir = Path.Combine(rootPath, ".archived");
            if (!Directory.Exists(archiveDir)) continue;

            foreach (var projDir in Directory.GetDirectories(archiveDir))
            {
                var statusPath = Path.Combine(projDir, ".godmode", "status.json");
                if (!File.Exists(statusPath)) continue;

                try
                {
                    var statusJson = File.ReadAllText(statusPath);
                    var status = JsonSerializer.Deserialize<ProjectStatus>(statusJson);
                    if (status == null) continue;

                    results.Add(new ProjectSummary(
                        ProjectId(profileName, rootName, Path.GetFileName(projDir)), status.Name, ProjectState.Stopped, status.UpdatedAt,
                        RootName: rootName, ProfileName: profileName));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read archived project at {Path}", projDir);
                }
            }
        }

        return Task.FromResult(results.ToArray());
    }

    public async Task<ProjectSummary> UnarchiveProjectAsync(string projectId)
    {
        // Find the archived project folder
        var snap = _snapshot;
        foreach (var (profileName, rootName, rootPath) in AllRoots(snap))
        {
            var archiveDir = Path.Combine(rootPath, ".archived");
            if (!Directory.Exists(archiveDir)) continue;

            foreach (var projDir in Directory.GetDirectories(archiveDir))
            {
                if (ProjectId(profileName, rootName, Path.GetFileName(projDir)) != projectId) continue;
                var statusPath = Path.Combine(projDir, ".godmode", "status.json");
                if (!File.Exists(statusPath)) continue;

                try
                {
                    var statusJson = File.ReadAllText(statusPath);
                    var status = JsonSerializer.Deserialize<ProjectStatus>(statusJson)
                        ?? throw new InvalidDataException($"{statusPath} is empty");

                    // Move back to root directory
                    var destDir = Path.Combine(rootPath, Path.GetFileName(projDir));
                    if (Directory.Exists(destDir))
                        destDir = Path.Combine(rootPath, $"{Path.GetFileName(projDir)}-{DateTime.UtcNow:yyyyMMddHHmmss}");
                    Directory.Move(projDir, destDir);

                    // Remove archive metadata
                    var archiveMeta = Path.Combine(destDir, ".godmode", "archive.json");
                    if (File.Exists(archiveMeta)) File.Delete(archiveMeta);

                    // Re-register project, under the folder it returned to
                    var id = ProjectId(profileName, rootName, Path.GetFileName(destDir));
                    var project = new ProjectInfo
                    {
                        Status = status with { Id = id, State = ProjectState.Stopped, RootName = rootName, ProfileName = profileName, OutputOffset = OutputLog.End(destDir) },
                        ProjectPath = destDir,
                        ActionName = null,
                        ProfileName = profileName,
                    };
                    _projects[id] = project;
                    await _statusUpdater.SaveStatusAsync(project);
                    ResumeChecks(project);

                    _logger.LogInformation("Unarchived project {ArchivedId} ({Name}) as {ProjectId}", projectId, status.Name, id);
                    await PushAttentionIfChangedAsync();

                    return new ProjectSummary(id, status.Name, ProjectState.Stopped,
                        DateTime.UtcNow, RootName: rootName, ProfileName: profileName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to unarchive project at {Path}", projDir);
                    throw;
                }
            }
        }

        throw new KeyNotFoundException($"Archived project {projectId} not found");
    }

    public async Task ResumeProjectAsync(string projectId)
    {
        if (!_projects.TryGetValue(projectId, out var project))
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        // Check if process is actually still running (regardless of reported state)
        if (_lifecycle.IsRunning(project))
        {
            _logger.LogInformation("Project {ProjectId} already has a running process with PID {ProcessId} (state: {State})",
                projectId, project.Process.ProcessId, project.Status.State);

            if (project.Status.State == ProjectState.Idle)
            {
                _logger.LogInformation("Project {ProjectId} is idle with running process, sending continue prompt", projectId);
                await _lifecycle.SendInputAsync(project, "Continue");
                await NotifyStatusChanged(project);
                return;
            }

            return;
        }

        // Process is not running - check if state needs correction
        if (project.Status.State is ProjectState.Running or ProjectState.WaitingInput or ProjectState.WaitingPermission)
        {
            _logger.LogWarning("Project {ProjectId} was marked as {State} but process is not running, resetting state",
                projectId, project.Status.State);
            project.Status = project.Status with { State = ProjectState.Stopped, PendingPermission = null, PendingQuestion = null };
        }

        if (project.Status.State is not (ProjectState.Stopped or ProjectState.Idle or ProjectState.Error))
        {
            throw new InvalidOperationException($"Project {projectId} cannot be resumed (current state: {project.Status.State})");
        }

        _logger.LogInformation("Resuming project {ProjectId} with session {SessionId}",
            projectId, project.SessionId);

        await _lifecycle.UpdateStatusAsync(project, status => status with { State = ProjectState.Running, LastError = null, UpdatedAt = DateTime.UtcNow });

        try
        {
            // Cancels the previous launch's token and gives this one its own
            await _lifecycle.ResumeAsync(project, BuildLaunchSpec(project));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume Claude process for project {ProjectId}", projectId);
            await _lifecycle.UpdateStatusAsync(project, status => status with
            {
                State = ProjectState.Error,
                LastError = ex is LaunchConfigException ? ex.Message : status.LastError,
            });
            await NotifyStatusChanged(project);
            throw;
        }

        await NotifyStatusChanged(project);
    }

    public async Task SubscribeProjectAsync(string projectId, long fromOffset, string connectionId)
    {
        if (!_projects.TryGetValue(projectId, out var project))
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        project.SubscribedConnections.Add(connectionId);
        await _lifecycle.SubscribeAsync(project, fromOffset, connectionId);
    }

    public async Task UnsubscribeProjectAsync(string projectId, string connectionId)
    {
        if (!_projects.TryGetValue(projectId, out var project))
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        project.SubscribedConnections.Remove(connectionId);
    }

    public async Task CleanupConnectionAsync(string connectionId)
    {
        foreach (var project in _projects.Values)
        {
            project.SubscribedConnections.Remove(connectionId);
        }
        await Task.CompletedTask;
    }

    public Task CreateProfileAsync(string name, string? description)
    {
        _profileFileManager.CreateProfile(name, description);
        RebuildSnapshot();
        return Task.CompletedTask;
    }

    public async Task DeleteProfileAsync(string name, bool deleteContents = false)
    {
        _profileFileManager.DeleteProfile(name);

        var snap = _snapshot;
        var profileRoots = snap.RootLookup
            .Where(kvp => string.Equals(kvp.Key.Item1, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (deleteContents)
        {
            // Cascade: stop processes, remove tracking, delete root directories
            foreach (var ((_, rootName), rootPath) in profileRoots)
            {
                // Stop and remove all projects under this root
                var projectsInRoot = _projects.Values
                    .Where(p => IsSameOrUnder(p.ProjectPath, rootPath))
                    .ToList();
                foreach (var project in projectsInRoot)
                {
                    project.Process.Cancellation?.Cancel();
                    project.Process.Output.TryComplete();
                    _projects.TryRemove(project.Status.Id, out _);
                    await _pullRequests.ForgetAsync(project.Status.Id);
                }

                // Delete the entire root directory (including projects)
                if (Directory.Exists(rootPath))
                {
                    _logger.LogInformation("Cascade deleting root '{RootName}' at {RootPath}", rootName, rootPath);
                    Directory.Delete(rootPath, recursive: true);
                }
            }
        }
        else
        {
            // Move roots to Default profile by clearing profileName from config
            foreach (var ((_, _), rootPath) in profileRoots)
            {
                var configPath = Path.Combine(rootPath, ".godmode-root", "config.json");
                if (!File.Exists(configPath)) continue;
                try
                {
                    var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(configPath))?.AsObject();
                    if (json != null)
                    {
                        json.Remove("profileName");
                        File.WriteAllText(configPath, json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    }
                }
                catch { /* skip unparseable configs */ }
            }
        }

        RebuildSnapshot();
        // A cascade removed projects, which may have needed the user
        await PushAttentionIfChangedAsync();
    }

    public Task UpdateProfileDescriptionAsync(string name, string? description)
    {
        _profileFileManager.UpdateProfileDescription(name, description);
        RebuildSnapshot();
        return Task.CompletedTask;
    }

    private static readonly JsonSerializerOptions CaseInsensitiveOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task RecoverProjectsAsync()
    {
        // Rebuild snapshot to include autodiscovered roots before recovery
        if (_projectRootsDir != null)
            RebuildSnapshot();

        var recoverSnap = _snapshot;
        _logger.LogInformation("Recovering projects from all project roots");

        // Each root's own folders, so the root is known exactly: "root" is not a prefix match for
        // "root2". A folder two roots share (two profiles naming one path) is recovered once
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var projectPaths = AllRoots(recoverSnap)
            .SelectMany(root => recoverSnap.ProjectFiles.ListProjectPaths(CompositeKey(root.Profile, root.Root))
                .Select(path => (Path: path, root.Profile, root.Root)))
            .Where(project => seen.Add(project.Path))
            .ToList();

        // Process all projects in parallel for faster startup
        await Parallel.ForEachAsync(projectPaths, async (found, ct) =>
        {
            var (projectPath, profileName, rootName) = found;
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
                var stateChanged = status.State is ProjectState.Running or ProjectState.WaitingInput or ProjectState.WaitingPermission;
                // A permission prompt ended with the process that asked: its bridge's call failed with the server
                status = status with { PendingPermission = null, PendingQuestion = null };

                // The ID is where the folder is. One written before IDs carried the profile and root
                // (the bare folder name), or before its root moved profile, is migrated: status.json
                // is rewritten below. Nothing else in .godmode holds the ID
                var id = ProjectId(profileName, rootName, Path.GetFileName(projectPath));
                var idChanged = status.Id != id;
                if (idChanged)
                    _logger.LogInformation("Project at {Path} had ID {OldId}; it is now {ProjectId}", projectPath, status.Id, id);

                // The offset is output.jsonl's, not status.json's, which is saved less often than output is written
                var outputOffset = OutputLog.End(projectPath);
                var correctedStatus = stateChanged
                    ? status with { Id = id, State = ProjectState.Stopped, UpdatedAt = DateTime.UtcNow, RootName = rootName, ProfileName = profileName, OutputOffset = outputOffset }
                    : status with { Id = id, RootName = rootName, ProfileName = profileName, OutputOffset = outputOffset };

                var project = new ProjectInfo
                {
                    Status = correctedStatus,
                    ProjectPath = projectPath,
                    ProfileName = profileName
                };

                // Load action name from settings
                var settings = ProjectFiles.ProjectSettings.Load(projectPath);
                project.ActionName = settings.ActionName;

                project.SessionId = await SessionIdFile.ReadAsync(projectPath, ct);

                _projects[project.Status.Id] = project;

                // Only save if state or ID changed
                if (stateChanged || idChanged)
                {
                    await _statusUpdater.SaveStatusAsync(project);
                }
                ResumeChecks(project);

                _logger.LogInformation("Recovered project {ProjectId} ({Name})", project.Status.Id, project.Status.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recover project from {Path}", projectPath);
            }
        });

        await PushAttentionIfChangedAsync();
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
    /// Uses {rootPath}/logs/{folder}.log so it survives create script's project dir delete.
    /// </summary>
    private static string GetScriptLogPath(string rootPath, string folder)
    {
        var logsDir = Path.Combine(rootPath, "logs");
        Directory.CreateDirectory(logsDir);
        return Path.Combine(logsDir, $"{folder}.log");
    }

    /// <summary>
    /// Returns a result file path for script-to-server communication.
    /// Scripts can write key=value pairs (e.g. project_path, project_name) to override defaults.
    /// </summary>
    private static string GetResultFilePath(string rootPath, string folder)
    {
        var logsDir = Path.Combine(rootPath, "logs");
        Directory.CreateDirectory(logsDir);
        return Path.Combine(logsDir, $"{folder}.result");
    }

    /// <summary>
    /// The full path and folder name of a create script's <c>project_path</c>. Refused when it is
    /// the root or above it, or its folder name is not one of its own (<c>x/..</c>): a delete of the
    /// project would delete that recursively.
    /// </summary>
    private static (string Path, string Folder) ValidateScriptProjectPath(string projectPath, string rootPath)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath));
        if (IsSameOrUnder(rootPath, fullPath))
            throw new ArgumentException($"The create script's project_path '{projectPath}' is the project root or above it.");
        var folder = Path.GetFileName(fullPath);
        ProjectFiles.ProjectFolder.ValidateFolderName(folder, "project_path");
        return (fullPath, folder);
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
        {
            var resolved = TemplateResolver.Resolve(action.NameTemplate, inputs);
            // If unresolved placeholders remain (e.g. the caller didn't provide all inputs),
            // fall back to a timestamp-based name
            if (resolved != null && resolved.Contains('{') && resolved.Contains('}'))
            {
                var fallback = TemplateResolver.GetString(inputs, "name");
                return fallback ?? $"project-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            }
            return resolved;
        }

        return TemplateResolver.GetString(inputs, "name");
    }

    /// <summary>
    /// Resolves the initial prompt from inputs or promptTemplate.
    /// </summary>
    private static string? ResolvePrompt(CreateAction action, Dictionary<string, JsonElement> inputs)
    {
        if (action.PromptTemplate != null)
        {
            var resolved = TemplateResolver.Resolve(action.PromptTemplate, inputs);
            // If unresolved placeholders remain, return null (no prompt)
            if (resolved != null && resolved.Contains('{') && resolved.Contains('}'))
                return TemplateResolver.GetString(inputs, "prompt");
            return resolved;
        }

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
    /// The one place a claude launch is configured, for create and resume alike: everything comes
    /// from the project (its profile, root, action and model) and what is saved in its folder
    /// (settings.json), read fresh, so a resume, or a resume after a restart, launches as the
    /// create did. The profile's environment and MCP servers, the action's, and the MCP bridge with
    /// its <c>GODMODE_*</c> variables and a fresh project token are always there. A root config that
    /// cannot be read, or an action that is gone, throws <see cref="LaunchConfigException"/>: a
    /// launch with anything but the project's own action would not be the one it was created with.
    /// </summary>
    private ClaudeLaunchSpec BuildLaunchSpec(ProjectInfo project)
    {
        var snap = _snapshot;
        var settings = ProjectFiles.ProjectSettings.Load(project.ProjectPath);
        // Recovery reads the action name from settings too; a project created before it was saved has none
        project.ActionName ??= settings.ActionName;
        var profileName = project.ProfileName ?? project.Status.ProfileName;
        snap.Profiles.TryGetValue(profileName ?? "", out var profile);

        var (action, stripEnvVarProfile) = ResolveLaunchAction(snap, project, profileName);
        // Never logged: it carries the MCP servers' credentials
        var mcpConfigJson = InjectMcpBridge(BuildMcpConfigJson(profile?.McpServers, action.McpServers));
        var (env, args) = BuildClaudeConfig(project.ProjectPath, action, settings, project.Status.Model ?? action.Model,
            profile?.Environment, profileName, stripEnvVarProfile, mcpConfigJson);
        return new ClaudeLaunchSpec(AddMcpBridgeEnvironment(project, env), args ?? []);
    }

    /// <summary>
    /// The project's action in its root's config (the default action for a project with no root).
    /// Throws <see cref="LaunchConfigException"/> when the config cannot be read or lacks the action.
    /// </summary>
    private (CreateAction Action, bool StripEnvVarProfile) ResolveLaunchAction(ProfileSnapshot snap, ProjectInfo project, string? profileName)
    {
        if (project.Status.RootName == null || profileName == null) return (new CreateAction("Create"), false);

        RootConfig config;
        try
        {
            config = _rootConfigReader.ReadConfigStrict(snap.ProjectFiles.GetProjectRootPath(CompositeKey(profileName, project.Status.RootName)));
        }
        catch (Exception ex)
        {
            throw new LaunchConfigException($"root config unreadable: {ex.Message}", ex);
        }
        return config.ResolveAction(project.ActionName) is { } action
            ? (action, config.StripEnvVarProfile)
            : throw new LaunchConfigException($"root config has no action '{project.ActionName}'");
    }

    /// <summary>
    /// Builds claude environment and args from action config + project settings + profile env.
    /// </summary>
    private static (Dictionary<string, string>? Env, string[]? Args) BuildClaudeConfig(
        string projectPath, CreateAction action, ProjectFiles.ProjectSettings settings,
        string? model = null,
        Dictionary<string, string>? profileEnv = null,
        string? profileName = null,
        bool stripEnvVarProfile = false,
        string? mcpConfigJson = null)
    {
        var env = MergeAndExpandEnvironment(profileEnv, action.Environment, profileName, stripEnvVarProfile);

        var args = new List<string>();
        if (action.ClaudeArgs != null)
            args.AddRange(action.ClaudeArgs);
        if (settings.DangerouslySkipPermissions)
            args.Add("--dangerously-skip-permissions");

        // Tool calls that need approval wait for the user's answer through the bridge. It also makes
        // claude offer AskUserQuestion in --print mode, skip-permissions or not, and ask it the same way
        args.Add("--permission-prompts");
        args.Add("host");
        args.Add("--permission-prompt-tool");
        args.Add(PermissionPromptTool);
        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("--model");
            args.Add(model);
        }
        if (!string.IsNullOrWhiteSpace(mcpConfigJson))
        {
            // --mcp-config expects a file path, not inline JSON; the process manager deletes it on exit
            args.Add("--mcp-config");
            args.Add(McpConfigFile.Write(projectPath, mcpConfigJson));
        }

        // Auto-allow all MCP tools so Claude doesn't block on permissions in --print mode.
        // --allowedTools requires server-level prefixes (e.g. "mcp__jira"), not just "mcp__".
        if (!settings.DangerouslySkipPermissions)
        {
            var mcpPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // From our --mcp-config
            if (!string.IsNullOrWhiteSpace(mcpConfigJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(mcpConfigJson);
                    if (doc.RootElement.TryGetProperty("mcpServers", out var servers))
                        foreach (var server in servers.EnumerateObject())
                            mcpPrefixes.Add($"mcp__{server.Name}");
                }
                catch { /* ignore */ }
            }

            // From user's local ~/.claude/settings.json (covers pencil, grafana, etc.)
            var userSettingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
            if (File.Exists(userSettingsPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(userSettingsPath));
                    if (doc.RootElement.TryGetProperty("mcpServers", out var localServers))
                        foreach (var server in localServers.EnumerateObject())
                            mcpPrefixes.Add($"mcp__{server.Name}");
                }
                catch { /* ignore */ }
            }

            if (mcpPrefixes.Count > 0)
            {
                args.Add("--allowedTools");
                args.Add(string.Join(",", mcpPrefixes));
            }
        }

        return (env, args.Count > 0 ? args.ToArray() : null);
    }

    /// <summary>
    /// Merges MCP servers from profile and action levels and returns inline JSON for --mcp-config.
    /// Returns the JSON string if MCP servers exist, null otherwise.
    /// Merge order: profile → action (action wins on conflict).
    /// Expands ${VAR} references in env values.
    /// </summary>
    private static string? BuildMcpConfigJson(
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
        // Stdio servers → { command, args, env }; URL servers → { type, url, headers }
        var mcpConfig = new Dictionary<string, object>
        {
            ["mcpServers"] = merged.ToDictionary(
                kvp => kvp.Key,
                kvp =>
                {
                    if (!string.IsNullOrEmpty(kvp.Value.Url))
                    {
                        // URLs ending in /sse use legacy SSE transport; all others use streamable HTTP
                        var transport = kvp.Value.Url.EndsWith("/sse", StringComparison.OrdinalIgnoreCase) ? "sse" : "http";
                        var server = new Dictionary<string, object>
                        {
                            ["type"] = transport,
                            ["url"] = kvp.Value.Url
                        };
                        var headers = ExpandEnvVars(kvp.Value.Headers);
                        if (headers is { Count: > 0 })
                            server["headers"] = headers;
                        return (object)server;
                    }
                    {
                        var env = ExpandEnvVars(kvp.Value.Env);
                        if (env is { Count: > 0 })
                            return (object)new { command = kvp.Value.Command ?? "", args = kvp.Value.Args ?? [], env };
                        return (object)new { command = kvp.Value.Command ?? "", args = kvp.Value.Args ?? Array.Empty<string>() };
                    }
                })
        };

        return JsonSerializer.Serialize(mcpConfig);
    }

    /// <summary>
    /// Sets the env vars the GodMode MCP bridge calls back to this server with, issuing a fresh
    /// project token for this launch. Tokens live only in memory, so a project recovered after a
    /// restart has none until it is launched again; a new launch also retires the previous token.
    /// They are not persisted: shutdown kills every claude, and one that outlives a crash has lost
    /// its pipes (its output reaches no server, its stdin is closed, so it ends with its turn) and
    /// is not a process the next server tracks, so an old token would authorise nothing useful.
    /// </summary>
    private Dictionary<string, string> AddMcpBridgeEnvironment(ProjectInfo project, Dictionary<string, string>? env)
    {
        project.ProjectToken = GenerateProjectToken();
        env ??= new Dictionary<string, string>();
        env["GODMODE_PROJECT_ID"] = project.Status.Id;
        env["GODMODE_PROJECT_TOKEN"] = project.ProjectToken;
        env["GODMODE_SERVER_URL"] = ServerUrl();
        return env;
    }

    /// <summary>
    /// Injects the GodMode MCP bridge server into an MCP config JSON string.
    /// The bridge env vars (GODMODE_PROJECT_ID, etc.) are inherited from the Claude process env.
    /// </summary>
    private string InjectMcpBridge(string? existingJson)
    {
        var bridgePath = _mcpBridgePath;

        Dictionary<string, object>? mcpConfig;
        Dictionary<string, object> servers;

        if (!string.IsNullOrEmpty(existingJson))
        {
            mcpConfig = JsonSerializer.Deserialize<Dictionary<string, object>>(existingJson);
            if (mcpConfig != null && mcpConfig.TryGetValue("mcpServers", out var serversObj) && serversObj is JsonElement el)
            {
                servers = JsonSerializer.Deserialize<Dictionary<string, object>>(el.GetRawText()) ?? new();
            }
            else
            {
                servers = new();
                mcpConfig ??= new();
            }
        }
        else
        {
            mcpConfig = new();
            servers = new();
        }

        // Add the bridge as a stdio MCP server — env vars are inherited from the Claude process
        servers[McpBridgeServerName] = new
        {
            command = "node",
            args = new[] { bridgePath },
            env = new Dictionary<string, string>()
        };

        mcpConfig["mcpServers"] = servers;
        return JsonSerializer.Serialize(mcpConfig);
    }

    /// <summary>
    /// The bridge bundle every session runs: <see cref="McpBridgePathSetting"/> (or the
    /// GODMODE_MCP_BRIDGE_PATH environment variable), else where the server build puts it. Throws
    /// when it is not there: a session without it cannot ask for permission, and would deny every
    /// tool call that needs approval without asking.
    /// </summary>
    private static string ResolveMcpBridgePath(IConfiguration configuration)
    {
        var configured = configuration[McpBridgePathSetting] is { Length: > 0 } setting ? setting
            : Environment.GetEnvironmentVariable("GODMODE_MCP_BRIDGE_PATH") is { Length: > 0 } env ? env
            : null;
        var path = Path.GetFullPath(configured ?? Path.Combine(AppContext.BaseDirectory, BundledMcpBridge));
        if (!File.Exists(path))
            throw new FileNotFoundException(configured != null
                ? $"The GodMode MCP bridge is not at {path}, where {McpBridgePathSetting} (or GODMODE_MCP_BRIDGE_PATH) points."
                : $"The GodMode MCP bridge is not at {path}. The server build puts it there (src/GodMode.McpBridge, " +
                  "built with -p:BuildMcpBridge left on), or set " + McpBridgePathSetting + " to its godmode-mcp-bridge.cjs.", path);
        return path;
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
        // Scripts name branches and folders after it: the folder name, as before project IDs carried
        // the profile and root. Claude's own GODMODE_PROJECT_ID, for the MCP bridge, is the project ID
        env["GODMODE_PROJECT_ID"] = Path.GetFileName(project.ProjectPath);
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

    private Task NotifyStatusChanged(ProjectInfo project) => _lifecycle.NotifyStatusChangedAsync(project);

    // ── Internal API helpers (project tokens, result storage) ──

    /// <summary>
    /// Generates a cryptographically random project-scoped token for MCP bridge auth.
    /// </summary>
    private static string GenerateProjectToken()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        return "gpt_" + Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// The URL the bridge calls this server on, from the addresses it is bound to once started
    /// (port 0 resolved, --urls and URLS applied), else the configured Urls: see <see cref="BridgeUrl"/>.
    /// </summary>
    private string ServerUrl() =>
        BridgeUrl.From(_server?.Features.Get<IServerAddressesFeature>()?.Addresses is { Count: > 0 } bound ? bound : _configuredUrls);

    /// <summary>
    /// Validates a project token and returns the project info if valid.
    /// Used by internal API endpoints.
    /// </summary>
    public ProjectInfo? ValidateProjectToken(string projectId, string token)
    {
        if (!_projects.TryGetValue(projectId, out var project))
            return null;

        if (string.IsNullOrEmpty(project.ProjectToken))
            return null;

        var storedBytes = System.Text.Encoding.UTF8.GetBytes(project.ProjectToken);
        var providedBytes = System.Text.Encoding.UTF8.GetBytes(token);

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(storedBytes, providedBytes)
            ? project
            : null;
    }

    /// <summary>
    /// Stores a structured result for a project (called by MCP bridge via internal API).
    /// </summary>
    public async Task StoreProjectResultAsync(string projectId, SubmitResultRequest resultRequest)
    {
        if (!_projects.TryGetValue(projectId, out var project))
            throw new KeyNotFoundException($"Project {projectId} not found");

        var result = new ProjectResult(resultRequest.Result, resultRequest.Summary, DateTime.UtcNow);
        var resultPath = Path.Combine(project.ProjectPath, ".godmode", "result.json");
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        await File.WriteAllTextAsync(resultPath, json);

        _logger.LogInformation("Project {ProjectId} submitted result ({Summary})",
            projectId, resultRequest.Summary ?? "no summary");

        // Notify clients of the result
        await NotifyStatusChanged(project);
    }

    /// <summary>
    /// Updates the custom status message for a project (called by MCP bridge).
    /// </summary>
    public async Task UpdateCustomStatusAsync(string projectId, string message)
    {
        if (!_projects.TryGetValue(projectId, out var project))
            throw new KeyNotFoundException($"Project {projectId} not found");

        project.CustomStatus = message;
        _logger.LogInformation("Project {ProjectId} custom status: {Status}", projectId, message);

        await NotifyStatusChanged(project);
    }

    /// <summary>
    /// Requests human review for a project (called by MCP bridge).
    /// Puts the project into WaitingInput state with the review question.
    /// </summary>
    public async Task RequestHumanReviewAsync(string projectId, RequestReviewRequest reviewRequest)
    {
        if (!_projects.TryGetValue(projectId, out var project))
            throw new KeyNotFoundException($"Project {projectId} not found");

        var question = string.IsNullOrEmpty(reviewRequest.Context)
            ? reviewRequest.Question
            : $"{reviewRequest.Question}\n\nContext: {reviewRequest.Context}";

        await _lifecycle.UpdateStatusAsync(project, status => status with
        {
            State = ProjectState.WaitingInput,
            CurrentQuestion = question,
            QuestionAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await NotifyStatusChanged(project);

        _logger.LogInformation("Project {ProjectId} requested human review: {Question}", projectId, reviewRequest.Question);
    }
}
