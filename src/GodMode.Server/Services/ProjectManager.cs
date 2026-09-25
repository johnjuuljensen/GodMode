using GodMode.Server.Auth;
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
    /// <summary>How long server shutdown waits for the projects' processes to be stopped and marked Stopped.</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(15);

    /// <summary>What a shutdown keeps of <see cref="ShutdownTimeout"/> for killing what its grace period did not stop.</summary>
    private static readonly TimeSpan ShutdownKillMargin = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long before a shutdown a process may have exited on its own and still count as stopped
    /// by it (a Ctrl+C reaches claude too): see <see cref="ProjectLifecycle.UndoExitBeforeShutdownAsync"/>.
    /// </summary>
    public const string ExitBeforeShutdownWindowSetting = "ExitBeforeShutdownWindowSeconds";
    private readonly TimeSpan _exitBeforeShutdownWindow;

    /// <summary>How many projects the start resumes at once: each is one claude process starting its session.</summary>
    private const int ConcurrentResumes = 3;

    /// <summary>ApplicationStopping: the start stops carrying on with interrupted projects.</summary>
    private readonly CancellationToken _stopping;

    /// <summary>The name GodMode's own MCP server, this server's <c>/mcp</c> endpoint, has in every session's MCP config.</summary>
    internal const string McpServerName = "godmode";

    /// <summary>
    /// The tool claude asks for permission with (--permission-prompt-tool), and puts its
    /// AskUserQuestion calls to: see <see cref="RequestPermissionAsync"/>.
    /// </summary>
    internal const string PermissionPromptTool = $"mcp__{McpServerName}__{Services.PermissionPromptTool.Name}";

    private readonly ProjectLifecycle _lifecycle;
    private readonly IStatusUpdater _statusUpdater;
    private readonly IRootConfigReader _rootConfigReader;
    private readonly IScriptRunner _scriptRunner;
    private readonly IHubContext<ProjectHub, IProjectHubClient> _hubContext;
    private readonly ProfileFileManager _profileFileManager;
    private readonly ILogger<ProjectManager> _logger;
    private readonly ConcurrentDictionary<string, ProjectInfo> _projects = new();
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
        _server = server;
        _configuredUrls = (configuration["Urls"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _sessionStartTimeout = TimeSpan.FromSeconds(configuration.GetValue(SessionStartTimeoutSetting, 60.0));
        _statusScriptTimeout = TimeSpan.FromSeconds(configuration.GetValue(StatusScriptTimeoutSetting, 30.0));
        _exitBeforeShutdownWindow = TimeSpan.FromSeconds(configuration.GetValue(ExitBeforeShutdownWindowSetting, 5.0));
        _pullRequests = new PullRequestPoller(CheckPullRequestAsync,
            TimeSpan.FromSeconds(configuration.GetValue(PullRequestPollSetting, 600.0)), logger);
        _lifecycle.StatusNotified += OnStatusNotifiedAsync;

        // Read optional autodiscovery directory (normalize empty/whitespace to null)
        var rawDir = configuration["ProjectRootsDir"];
        _projectRootsDir = string.IsNullOrWhiteSpace(rawDir) ? null : rawDir;

        if (_projectRootsDir != null)
            _logger.LogInformation("Autodiscovery enabled: scanning {ProjectRootsDir} for .godmode-root/ directories", _projectRootsDir);

        // Build initial profile/root snapshot
        _snapshot = BuildSnapshot();

        _stopping = lifetime.ApplicationStopping;
        lifetime.ApplicationStopping.Register(StopProjectsOnShutdown);
    }

    /// <summary>
    /// Server shutdown: stops every claude as a Stop does, all at once (interrupted, then its process
    /// tree killed if it has not exited within the grace period, shortened to leave time for that),
    /// and persists Stopped, so recovery on the next start does not launch a second process on a
    /// session an orphan still runs. A project that was working or waiting on the user keeps that in
    /// <see cref="ProjectStatus.StateAtShutdown"/>, and the next start resumes it
    /// (<see cref="ResumeInterruptedProjectsAsync"/>). A process that exits on its own now counts as
    /// stopped, one handled just before is taken back (a server whose sessions share its console once
    /// lost them to its terminal's Ctrl+C), and its project is stopped like the rest, so the exit is
    /// persisted before the server goes. Blocks shutdown until done or <see cref="ShutdownTimeout"/> passes.
    /// </summary>
    private void StopProjectsOnShutdown()
    {
        // Before the projects stop: going Stopped starts no status script now
        _pullRequests.Stop();
        _lifecycle.BeginShutdown();
        // What each was doing as the shutdown began, before stopping it changes that
        var running = _projects.Values
            .Where(project => project.Process.ProcessId != 0
                || project.Status.State is ProjectState.Running or ProjectState.WaitingInput or ProjectState.WaitingPermission or ProjectState.Idle)
            .Select(project => (Project: project, StateAtShutdown: ProjectLifecycle.ActiveState(project.Status.State)))
            .ToArray();
        var exited = _projects.Values.Except(running.Select(r => r.Project)).ToArray();
        if (running.Length == 0 && exited.Length == 0) return;

        _logger.LogInformation("Server stopping: stopping {Count} running project(s)", running.Length);
        var grace = TimeSpan.FromTicks(Math.Min(_lifecycle.StopGracePeriod.Ticks, (ShutdownTimeout - ShutdownKillMargin).Ticks));
        var stops = Task.WhenAll(
            running.Select(r => StopOnShutdownAsync(r.Project, async () =>
            {
                await _lifecycle.StopAsync(r.Project, r.StateAtShutdown, grace);
                return true;
            })).Concat(exited.Select(project => StopOnShutdownAsync(project, async () =>
            {
                if (!await _lifecycle.UndoExitBeforeShutdownAsync(project, _exitBeforeShutdownWindow)) return false;
                _logger.LogInformation("Project {ProjectId}: claude exited just before the server stopped; it is stopped by the shutdown instead",
                    project.Status.Id);
                return true;
            }))));
        if (!stops.Wait(ShutdownTimeout))
            _logger.LogWarning("Server stopping: projects were not all stopped within {Timeout}", ShutdownTimeout);
    }

    /// <summary>Runs <paramref name="stop"/>, pushing the status when it changed it; a failure is logged, and stops no other project.</summary>
    private async Task StopOnShutdownAsync(ProjectInfo project, Func<Task<bool>> stop)
    {
        try
        {
            if (await stop()) await NotifyStatusChanged(project);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not stop project {ProjectId} on shutdown", project.Status.Id);
        }
    }

    /// <summary>
    /// Builds an immutable snapshot of all profile/root state: the profiles in .profiles/, with the
    /// roots autodiscovered in ProjectRootsDir. Thread-safe — can be called from any thread.
    /// </summary>
    private ProfileSnapshot BuildSnapshot()
    {
        // Layer 1: File-based profiles from .profiles/ directory
        var merged = new Dictionary<string, ProfileConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, data) in _profileFileManager.ReadAllProfiles())
        {
            merged[name] = new ProfileConfig
            {
                Roots = new Dictionary<string, string>(),
                Environment = data.Environment,
                Description = data.Description
            };
        }

        // Layer 2: Autodiscovered roots from ProjectRootsDir
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
        var projectFiles = new ProjectFiles.ProjectManager(compositeRoots);

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
                .Select(a => new CreateActionInfo(a.Name, a.Description, a.InputSchema, a.Model, a.AllowSkipPermissions))
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

    /// <summary>The server's record of a tracked project, for the tests; null when it is not tracked.</summary>
    internal ProjectInfo? Tracked(string projectId) => _projects.GetValueOrDefault(projectId);

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
        // Read as the launch reads it: a config file that cannot be read fails the create here, before
        // anything is written, rather than at its launch
        RootConfig config;
        try
        {
            config = _rootConfigReader.ReadConfigStrict(rootPath);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Root '{request.ProjectRootName}' has a config that cannot be read: {ex.Message}", ex);
        }
        var action = config.ResolveAction(request.ActionName)
            ?? throw new ArgumentException($"Action '{request.ActionName}' not found in root '{request.ProjectRootName}'.");

        // Skipping permissions is the root's to allow: a create cannot ask for what its root forbids
        var skipPermissions = GetBool(request.Inputs, "skipPermissions");
        if (skipPermissions && !action.AllowSkipPermissions)
            throw new ArgumentException(
                $"Root '{request.ProjectRootName}' does not allow Skip Permissions for action '{action.Name}': its config would need \"allowSkipPermissions\": true.");

        // Get profile environment for merging
        snap.Profiles.TryGetValue(request.ProfileName, out var profileConfig);
        var profileEnv = profileConfig?.Environment;

        // Resolve name from inputs or nameTemplate
        var name = ResolveProjectName(action, request.Inputs);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Project name is required. Provide a 'name' input or configure nameTemplate in .godmode-root/config.json.");

        // Resolve prompt from inputs or promptTemplate
        var prompt = ResolvePrompt(action, request.Inputs);

        // The project's folder, decided before anything is written. A name that leaves no folder of
        // its own ("..", ".") is refused here, before any script runs or file is written
        var folder = ProjectFiles.ProjectManager.ConvertNameToPath(name);
        var reuseExisting = request.Inputs.TryGetValue("__reuseExisting", out var reuse) &&
                            reuse.ValueKind == System.Text.Json.JsonValueKind.True;
        var autoSuffix = request.Inputs.TryGetValue("__autoSuffix", out var suffix) &&
                         suffix.ValueKind == System.Text.Json.JsonValueKind.True;
        var suffixed = !action.ScriptsCreateFolder && !reuseExisting && autoSuffix && Directory.Exists(Path.Combine(rootPath, folder));
        if (suffixed)
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
        }
        var projectPath = Path.Combine(rootPath, folder);

        var (profileName, rootName) = ConfiguredNames(snap, request.ProfileName, request.ProjectRootName);
        var projectId = ProjectId(profileName, rootName, folder);

        // One project per ID and per folder: a tracked project's claude would be orphaned, and its
        // files overwritten. Claimed before a folder is reused or any script runs, until registered
        using var claims = new CreateClaims(this);
        claims.Claim(projectId, projectPath);

        // Unless the scripts create the project directory (e.g. git worktree add)
        if (!action.ScriptsCreateFolder)
        {
            if (reuseExisting)
                // Reuse existing folder — reinitialize .godmode state
                ProjectFiles.ProjectFolder.Reuse(rootPath, folder, name);
            else if (suffixed)
                ProjectFiles.ProjectFolder.Create(rootPath, folder, name);
            else
                // Server creates the project folder via ProjectFiles
                snap.ProjectFiles.CreateProject(compositeKey, name);
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
                RegisterFailedCreate(project, ex.Message);
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
                RegisterFailedCreate(project, ex.Message);
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
                // Nor may a script's folder be a tracked project's
                claims.Claim(ProjectId(profileName, rootName, folder), projectPath);
            }
            catch (Exception ex) when (ex is ArgumentException or ProjectInUseException)
            {
                // The project keeps the folder it was given, so a delete removes only that
                _logger.LogError("Create script for project {ProjectId} returned a project_path it cannot have: {Message}", projectId, ex.Message);
                RegisterFailedCreate(project, ex.Message);
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

        // Save project settings (persists across restarts, includes action name for delete/resume).
        // The permission mode is kept with the project, as its model is, so its resumes keep it
        var settings = new ProjectFiles.ProjectSettings(
            DangerouslySkipPermissions: skipPermissions,
            ActionName: action.Name,
            PermissionMode: action.PermissionMode);
        settings.Save(projectPath);

        // Save initial status
        await _statusUpdater.SaveStatusAsync(project);

        // Add to tracking with its launch in flight, under its resume lock (a new project's, so free):
        // a reply, resume or stop that finds it waits until the launch has its process or has failed
        var resumeLock = project.Process.ResumeLock;
        await resumeLock.WaitAsync();
        project.Process.BeginLaunching();
        try
        {
            if (!_projects.TryAdd(projectId, project))
                throw new ProjectInUseException(projectId, "a project with this ID exists");

            // Start Claude process, configured from what is saved above, exactly as a resume will be
            try
            {
                await _lifecycle.StartAsync(project, prompt ?? "Hello", BuildLaunchSpec(project));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start Claude process for project {ProjectId}", projectId);
                await _lifecycle.UpdateStatusAsync(project, status => status with { State = ProjectState.Error, LastError = ex.Message });
                throw;
            }
        }
        finally
        {
            project.Process.EndLaunching();
            resumeLock.Release();
        }

        return project.Status;
    }

    /// <summary>
    /// A create that failed before its launch: its project is Error, saying why, for the user to see
    /// and delete. It has the ID and folder the create claimed, so no tracked project has them.
    /// </summary>
    private void RegisterFailedCreate(ProjectInfo project, string reason)
    {
        project.Status = project.Status with { State = ProjectState.Error, LastError = reason, UpdatedAt = DateTime.UtcNow };
        if (!_projects.TryAdd(project.Status.Id, project))
            _logger.LogWarning("Project {ProjectId} failed to create, and another has its ID", project.Status.Id);
    }

    /// <summary>The IDs and folders (full paths) that creates in progress have claimed: see <see cref="CreateClaims"/>.</summary>
    private readonly ConcurrentDictionary<string, byte> _creatingIds = new();
    private readonly ConcurrentDictionary<string, byte> _creatingPaths = new(PathComparer);

    /// <summary>Paths compared as the OS compares them: on Windows, <c>Fix</c> is the folder <c>fix</c>.</summary>
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string FullPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// What one create has claimed, so no two creates make one project and none makes a tracked one:
    /// from before anything is written until the project is registered or the create has failed.
    /// Disposing it gives the claims up.
    /// </summary>
    private sealed class CreateClaims(ProjectManager manager) : IDisposable
    {
        private readonly List<string> _ids = [];
        private readonly List<string> _paths = [];

        /// <summary>
        /// Claims the ID and folder, or throws <see cref="ProjectInUseException"/> when a tracked
        /// project has either, or another create has claimed it.
        /// </summary>
        public void Claim(string projectId, string projectPath)
        {
            var path = FullPath(projectPath);
            if (!_ids.Contains(projectId))
            {
                if (!manager._creatingIds.TryAdd(projectId, 0))
                    throw new ProjectInUseException(projectId, "another create is making it");
                _ids.Add(projectId);
            }
            if (!_paths.Contains(path, PathComparer))
            {
                if (!manager._creatingPaths.TryAdd(path, 0))
                    throw new ProjectInUseException(projectId, $"another create is making {path}");
                _paths.Add(path);
            }
            if (manager._projects.ContainsKey(projectId))
                throw new ProjectInUseException(projectId, "a project with this ID exists");
            if (manager._projects.Values.FirstOrDefault(p => PathComparer.Equals(FullPath(p.ProjectPath), path)) is { } tracked)
                throw new ProjectInUseException(projectId, $"project {tracked.Status.Id} is in {path}");
        }

        public void Dispose()
        {
            foreach (var id in _ids) manager._creatingIds.TryRemove(id, out _);
            foreach (var path in _paths) manager._creatingPaths.TryRemove(path, out _);
            _ids.Clear();
            _paths.Clear();
        }
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

        // One reply at a time decides whether to resume: two would launch two processes. The wait
        // for the session to start comes after the lock, so a stop is not held behind it
        var reply = await WithResumeLockAsync(project, () => ReplyAndResumeLockedAsync(project, text, onlyIfInterrupted: false));
        if (reply.SessionStart is { } sessionStart) await sessionStart;
    }

    /// <summary>What a reply did under the resume lock, and, when it resumed, the wait for the session to start that follows.</summary>
    private readonly record struct ReplyOutcome(bool Delivered, Task? SessionStart = null);

    /// <summary>
    /// <see cref="ReplyAndResumeAsync"/>, under the project's resume lock. With
    /// <paramref name="onlyIfInterrupted"/> (the start carrying on after a shutdown), it sends
    /// nothing to a running claude and resumes only while the project still has its
    /// <see cref="ProjectStatus.StateAtShutdown"/>; not delivered when it did neither.
    /// </summary>
    private async Task<ReplyOutcome> ReplyAndResumeLockedAsync(ProjectInfo project, string text, bool onlyIfInterrupted)
    {
        var projectId = project.Status.Id;
        await _lifecycle.SettleAsync(project);
        if (_lifecycle.IsRunning(project))
        {
            if (onlyIfInterrupted) return new(false);
            await SendInputAsync(projectId, text);
            return new(true);
        }

        // claude writes system/init once it has read its first input, so the reply is sent at
        // once and the session start awaited after it
        var sessionStart = project.Process.NextSessionStart();
        if (!await TryResumeAsync(project, onlyIfInterrupted)) return new(false);
        var sentTo = await TrySendInputAsync(project, text);
        await NotifyStatusChanged(project);
        return new(true, AwaitSessionStartAsync(project, text, sessionStart, sentTo));
    }

    /// <summary>
    /// Waits for the resumed claude to start its session, and sends the reply again when the resume
    /// found no conversation and a fresh session took its place.
    /// </summary>
    private async Task AwaitSessionStartAsync(ProjectInfo project, string text, Task<int> sessionStart, int sentTo)
    {
        var projectId = project.Status.Id;
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

    private static async Task<T> WithResumeLockAsync<T>(ProjectInfo project, Func<Task<T>> action, CancellationToken cancel = default)
    {
        var resumeLock = project.Process.ResumeLock;
        await resumeLock.WaitAsync(cancel);
        try { return await action(); }
        finally { resumeLock.Release(); }
    }

    private static Task WithResumeLockAsync(ProjectInfo project, Func<Task> action) =>
        WithResumeLockAsync(project, async () => { await action(); return true; });

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

    /// <summary>A project that is back without a status push (recovered): its open pull request is checked, then polled.</summary>
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
            // claude cancelled the call, or its connection dropped (it exited or was killed): nobody is waiting for the answer
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

        // Before a launch or after it, never in the middle of one
        await WithResumeLockAsync(project, () => _lifecycle.StopAsync(project));
        await NotifyStatusChanged(project);
    }

    public async Task DeleteProjectAsync(string projectId, bool force = false)
    {
        if (!_projects.TryGetValue(projectId, out var project))
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        await WithResumeLockAsync(project, () => DeleteLockedAsync(project, force));
    }

    private async Task DeleteLockedAsync(ProjectInfo project, bool force)
    {
        var projectId = project.Status.Id;
        _logger.LogInformation("Deleting project {ProjectId} ({Name}), force={Force}", projectId, project.Status.Name, force);

        // Stopped before the delete scripts run, with nothing left waiting on the user: a prompt that
        // was waiting is denied, not shown again. A delete a script refuses leaves it so, and a
        // restart does not resume it. Its output pipeline stays open until the delete is committed
        await _lifecycle.StopAsync(project);
        await _lifecycle.UpdateStatusAsync(project, status => status with { CurrentQuestion = null });
        await NotifyStatusChanged(project);
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

    public async Task ResumeProjectAsync(string projectId)
    {
        if (!_projects.TryGetValue(projectId, out var project))
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        await WithResumeLockAsync(project, async () =>
        {
            // Check if process is actually still running (regardless of reported state)
            await _lifecycle.SettleAsync(project);
            if (_lifecycle.IsRunning(project))
            {
                _logger.LogInformation("Project {ProjectId} already has a running process with PID {ProcessId} (state: {State})",
                    projectId, project.Process.ProcessId, project.Status.State);

                if (project.Status.State == ProjectState.Idle)
                {
                    _logger.LogInformation("Project {ProjectId} is idle with running process, sending continue prompt", projectId);
                    await _lifecycle.SendInputAsync(project, "Continue");
                    await NotifyStatusChanged(project);
                }
                return;
            }

            // A resume with nothing to say: claude waits for input, and writes nothing until it has
            // some, so the project is Idle, resumed and waiting for the user, until then
            await TryResumeAsync(project, onlyIfInterrupted: false, resumedAs: ProjectState.Idle);
        });
    }

    /// <summary>
    /// Launches claude on the project's session, which has no process. Whether it may, and the claim
    /// that it is <paramref name="resumedAs"/>, are one step under the state lock, so a shutdown either
    /// comes before it (and it launches nothing) or after it (and stops what it launches). A claim
    /// respects a launch in flight (<see cref="ProjectProcess.Launching"/>) or a running process: it
    /// launches nothing then, and returns false. With <paramref name="onlyIfInterrupted"/>, only while
    /// <see cref="ProjectStatus.StateAtShutdown"/> is still set: false when it is not, and nothing is
    /// launched. Once the server is stopping it throws <see cref="ServerStoppingException"/>, leaving
    /// the project Stopped with its marker. A launch that fails leaves it Error, saying why.
    /// </summary>
    private async Task<bool> TryResumeAsync(ProjectInfo project, bool onlyIfInterrupted, ProjectState resumedAs = ProjectState.Running)
    {
        var projectId = project.Status.Id;
        ProjectState? interruptedAs = null;
        var claimed = false;
        try
        {
            await ClaimAsync();
        }
        catch
        {
            // The claim was not saved (status.json could not be replaced): nothing is launched, and
            // no launch is left in flight for the next claim, stop or reply to wait on
            if (claimed) project.Process.EndLaunching();
            throw;
        }
        if (!claimed) return false;

        _logger.LogInformation("Resuming project {ProjectId} with session {SessionId}", projectId, project.SessionId);
        try
        {
            // Cancels the previous launch's token and gives this one its own
            await _lifecycle.ResumeAsync(project, BuildLaunchSpec(project));
        }
        catch (ServerStoppingException)
        {
            // The shutdown began after the claim: the project is left as the shutdown leaves it, or it
            // would have been, keeping what it was interrupted as
            _logger.LogInformation("Project {ProjectId} was not resumed: the server is stopping", projectId);
            await _lifecycle.UpdateStatusAsync(project, status => status with
            {
                State = ProjectState.Stopped,
                StateAtShutdown = status.StateAtShutdown ?? interruptedAs,
            });
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume Claude process for project {ProjectId}", projectId);
            await _lifecycle.UpdateStatusAsync(project, status => status with { State = ProjectState.Error, LastError = ex.Message });
            await NotifyStatusChanged(project);
            throw;
        }
        finally
        {
            project.Process.EndLaunching();
        }

        await NotifyStatusChanged(project);
        return true;

        Task ClaimAsync() => _lifecycle.UpdateStatusAsync(project, status =>
        {
            if (_lifecycle.ShuttingDown) throw new ServerStoppingException();
            if (onlyIfInterrupted && status.StateAtShutdown == null) return status;

            // Another launch is in flight, or has its process: its Running is not stale
            if (project.Process.Launching || _lifecycle.IsRunning(project))
            {
                _logger.LogInformation("Project {ProjectId} is launching or running already; it is not launched again", projectId);
                return status;
            }

            // Process is not running - check if state needs correction
            if (status.State is ProjectState.Running or ProjectState.WaitingInput or ProjectState.WaitingPermission)
            {
                _logger.LogInformation("Project {ProjectId} was {State} but its process is not running, resetting state", projectId, status.State);
                status = status with { State = ProjectState.Stopped, PendingPermission = null, PendingQuestion = null };
            }
            if (status.State is not (ProjectState.Stopped or ProjectState.Idle or ProjectState.Error))
                throw new InvalidOperationException($"Project {projectId} cannot be resumed (current state: {status.State})");

            interruptedAs = status.StateAtShutdown;
            claimed = project.Process.BeginLaunching();
            if (!claimed) return status;
            // A launch settles what a shutdown left: the project is not resumed again on the next start
            return status with { State = resumedAs, LastError = null, StateAtShutdown = null, UpdatedAt = DateTime.UtcNow };
        });
    }

    public async Task SubscribeProjectAsync(string projectId, long fromOffset, string subscriptionId, string? generation, string connectionId)
    {
        if (!_projects.TryGetValue(projectId, out var project))
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        project.SubscribedConnections.Add(connectionId);
        await _lifecycle.SubscribeAsync(project, fromOffset, subscriptionId, generation, connectionId);
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
                // A permission prompt ended with the process that asked: its call to the MCP endpoint failed with the server
                status = status with { PendingPermission = null, PendingQuestion = null };

                // The ID is where the folder is. A status.json that says otherwise (its root moved
                // profile, or the folder moved) is rewritten below. Nothing else in .godmode holds the ID
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

    public async Task ResumeInterruptedProjectsAsync()
    {
        var interrupted = _projects.Values.Where(project => project.Status.StateAtShutdown != null).ToArray();
        if (interrupted.Length == 0) return;

        _logger.LogInformation("Carrying on with {Count} project(s) the last shutdown interrupted, {Concurrent} at a time",
            interrupted.Length, ConcurrentResumes);
        try
        {
            await Parallel.ForEachAsync(interrupted,
                new ParallelOptions { MaxDegreeOfParallelism = ConcurrentResumes, CancellationToken = _stopping },
                async (project, _) => await ResumeInterruptedAsync(project));
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Those not resumed yet keep their marker, for the start after this one
            _logger.LogInformation("The server is stopping: no more interrupted projects are resumed");
        }
    }

    /// <summary>
    /// One project a shutdown stopped while it was active, as its action says: with
    /// <c>resumeOnRestart</c> off it stays Stopped; one waiting on the user's answer is WaitingInput
    /// again, with its question and no process, until a reply resumes it (a continue prompt would
    /// answer it for the user); any other (working, or on a permission prompt the shutdown denied) is
    /// resumed and told <c>resumePrompt</c>, and waited for until claude starts its session. A resume
    /// that fails leaves the project Error, as any resume does; it is not tried again.
    /// It may have waited behind others, so what it does is decided when its turn comes, under its
    /// resume lock (which a reply takes) and the state lock (which a Stop takes): nothing, once the
    /// user has stopped, resumed or answered it, it is gone, or the server is stopping.
    /// </summary>
    private async Task ResumeInterruptedAsync(ProjectInfo project)
    {
        var id = project.Status.Id;
        try
        {
            var action = ResumeAction(project);
            var resumed = await WithResumeLockAsync<Task?>(project, async () =>
            {
                if (_stopping.IsCancellationRequested || !_projects.TryGetValue(id, out var current) || current != project) return null;

                if (!action.ResumeOnRestart || project.Status is { StateAtShutdown: ProjectState.WaitingInput, CurrentQuestion: not null })
                {
                    var state = action.ResumeOnRestart ? ProjectState.WaitingInput : ProjectState.Stopped;
                    var changed = false;
                    await _lifecycle.UpdateStatusAsync(project, status =>
                    {
                        if (status.StateAtShutdown == null || _lifecycle.ShuttingDown || _lifecycle.IsRunning(project)) return status;
                        changed = true;
                        return status with { State = state, StateAtShutdown = null };
                    });
                    if (!changed) return null;
                    _logger.LogInformation("Project {ProjectId} was interrupted when the server stopped; it is {State}", id, state);
                    await NotifyStatusChanged(project);
                    return null;
                }

                _logger.LogInformation("Project {ProjectId} was {StateAtShutdown} when the server stopped; resuming it", id, project.Status.StateAtShutdown);
                var reply = await ReplyAndResumeLockedAsync(project, action.ResumePrompt, onlyIfInterrupted: true);
                if (!reply.Delivered)
                    _logger.LogInformation("Project {ProjectId} was stopped or resumed since; it is left as it is", id);
                return reply.SessionStart;
            }, _stopping);
            // Its slot is held until its session has started, but not its lock
            if (resumed != null) await resumed;
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Project {ProjectId} could not carry on after the restart: {Reason}", id, ex.Message);
        }
    }

    /// <summary>
    /// The project's action as its root's config says now, for what a restart does with it; the
    /// defaults when it has none. A config that cannot be read gives the defaults too: the resume
    /// then refuses, saying why (<see cref="BuildLaunchSpec"/>).
    /// </summary>
    private CreateAction ResumeAction(ProjectInfo project)
    {
        var profileName = project.ProfileName ?? project.Status.ProfileName;
        if (project.Status.RootName == null || profileName == null) return new CreateAction("Create");
        try
        {
            var rootPath = _snapshot.ProjectFiles.GetProjectRootPath(CompositeKey(profileName, project.Status.RootName));
            return _rootConfigReader.ReadConfig(rootPath).ResolveAction(project.ActionName) ?? new CreateAction("Create");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Project {ProjectId}: its root config could not be read ({Reason}); resuming it as the default says",
                project.Status.Id, ex.Message);
            return new CreateAction("Create");
        }
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
    /// Ensures the .godmode directory exists in a project folder, and starts its output's generation.
    /// Called after scripts run (which may have created the project dir without .godmode).
    /// </summary>
    private static void EnsureGodModeDirectory(string projectPath)
    {
        var godModePath = Path.Combine(projectPath, ".godmode");
        if (!Directory.Exists(godModePath))
        {
            Directory.CreateDirectory(godModePath);
            File.WriteAllText(
                Path.Combine(godModePath, ".gitignore"),
                "# Exclude all GodMode state files\n*\n",
                System.Text.Encoding.UTF8);
        }

        // A new one each time: an ID created again starts a new generation, so no client resumes into it from an old offset
        OutputLog.StartGeneration(projectPath);
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
    /// create did. The profile's environment, the action's, and GodMode's MCP endpoint with a fresh
    /// project token are always there. That endpoint is the only MCP server GodMode gives claude: a
    /// repo brings its own in its <c>.mcp.json</c>, and user-scoped ones live in the profile's
    /// <c>CLAUDE_CONFIG_DIR</c>. A root config that cannot be read, or an action that is gone, throws
    /// <see cref="LaunchConfigException"/>: a launch with anything but the project's own action would
    /// not be the one it was created with.
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
        var (skipPermissions, permissionMode) = LaunchPermissions(project, settings, action);
        var (env, args) = BuildClaudeConfig(project.ProjectPath, action, skipPermissions, permissionMode, McpConfigJson(project, IssueProjectToken(project)),
            project.Status.Model ?? action.Model, profile?.Environment, profileName, stripEnvVarProfile);
        return new ClaudeLaunchSpec(env ?? new Dictionary<string, string>(), args);
    }

    /// <summary>
    /// How a launch is permitted, for create, resume, a reply's resume and a restart's alike. Skipping
    /// permissions needs both the project's settings to ask for it and its root's config, as it is now,
    /// to allow it: settings.json is in the project folder, which the session can write, and a planted
    /// or copied folder is relaunched from it unattended. The permission mode is the one the project was
    /// created with (its root's current one, for a project created without), and is checked again for
    /// the same reason; skipping overrides it. Whatever is ignored is logged once per project.
    /// </summary>
    private (bool SkipPermissions, string? PermissionMode) LaunchPermissions(
        ProjectInfo project, ProjectFiles.ProjectSettings settings, CreateAction action)
    {
        var skip = settings.DangerouslySkipPermissions;
        if (skip && !action.AllowSkipPermissions)
        {
            if (FirstLaunchWarning(project, "skip"))
                _logger.LogWarning("Project {ProjectId} asks to skip permissions, which its root does not allow (allowSkipPermissions): it launches without --dangerously-skip-permissions",
                    project.Status.Id);
            skip = false;
        }

        var kept = settings.PermissionMode ?? action.PermissionMode;
        var mode = kept == null ? null : PermissionModes.Canonical(kept);
        if (kept != null && mode == null && FirstLaunchWarning(project, "mode"))
            _logger.LogWarning("Project {ProjectId}: {Reason}; it launches without --permission-mode", project.Status.Id, PermissionModes.Refusal(kept));
        if (skip && mode != null)
        {
            if (FirstLaunchWarning(project, "mode-with-skip"))
                _logger.LogWarning("Project {ProjectId} skips permissions, so its permission mode {PermissionMode} is ignored", project.Status.Id, mode);
            mode = null;
        }
        return (skip, mode);
    }

    /// <summary>The launch warnings said so far, by project folder and what they are about: each is said once.</summary>
    private readonly ConcurrentDictionary<string, byte> _launchWarnings = new(PathComparer);

    private bool FirstLaunchWarning(ProjectInfo project, string about) =>
        _launchWarnings.TryAdd($"{FullPath(project.ProjectPath)}\n{about}", 0);

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
    /// Builds claude environment and args from action config + the launch's permissions + profile env.
    /// Nothing is pre-approved: a tool call that needs approval reaches the permission prompt, unless
    /// Claude Code's own settings, the permission mode, or skip-permissions, allow it.
    /// </summary>
    private static (Dictionary<string, string>? Env, string[] Args) BuildClaudeConfig(
        string projectPath, CreateAction action, bool skipPermissions, string? permissionMode, string mcpConfigJson,
        string? model = null,
        Dictionary<string, string>? profileEnv = null,
        string? profileName = null,
        bool stripEnvVarProfile = false)
    {
        var env = MergeAndExpandEnvironment(profileEnv, action.Environment, profileName, stripEnvVarProfile);

        var args = new List<string>();
        if (action.ClaudeArgs != null)
            args.AddRange(action.ClaudeArgs);
        if (skipPermissions)
            args.Add("--dangerously-skip-permissions");
        if (permissionMode != null)
        {
            args.Add("--permission-mode");
            args.Add(permissionMode);
        }

        // Tool calls that need approval wait for the user's answer through GodMode's MCP endpoint. It
        // also makes claude offer AskUserQuestion in --print mode, skip-permissions or not, and ask it the same way
        args.Add("--permission-prompts");
        args.Add("host");
        args.Add("--permission-prompt-tool");
        args.Add(PermissionPromptTool);
        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("--model");
            args.Add(model);
        }
        // --mcp-config expects a file path, not inline JSON; the process manager deletes it on exit
        args.Add("--mcp-config");
        args.Add(McpConfigFile.Write(projectPath, mcpConfigJson));

        return (env, args.ToArray());
    }

    /// <summary>
    /// Issues the project a fresh token for this launch. Tokens live only in memory, so a project
    /// recovered after a restart has none until it is launched again; a new launch also retires the
    /// previous token. They are not persisted: shutdown kills every claude, and one that outlives a
    /// crash has lost its pipes (its output reaches no server, its stdin is closed, so it ends with
    /// its turn) and is not a process the next server tracks, so an old token would authorise nothing useful.
    /// </summary>
    private static string IssueProjectToken(ProjectInfo project) => project.ProjectToken = GenerateProjectToken();

    /// <summary>
    /// The session's MCP config: GodMode's own server, this server's MCP endpoint, and nothing else.
    /// Its headers carry the project and its token, which claude sends on every call. The token is in
    /// no environment variable, only in this file, which lives as long as the process does.
    /// </summary>
    private string McpConfigJson(ProjectInfo project, string token) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["mcpServers"] = new Dictionary<string, object>
        {
            [McpServerName] = new
            {
                type = "http",
                url = McpEndpointUrlOfThisServer(),
                headers = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {token}",
                    [ProjectTokenAuthenticationHandler.ProjectIdHeader] = project.Status.Id,
                },
            },
        },
    });

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
        // the profile and root
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

    // ── The MCP endpoint's project tokens ──

    /// <summary>
    /// Generates a cryptographically random project-scoped token for the MCP endpoint.
    /// </summary>
    private static string GenerateProjectToken()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        return "gpt_" + Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// The URL claude calls this server's MCP endpoint on, from the addresses it is bound to once
    /// started (port 0 resolved, --urls and URLS applied), else the configured Urls: see <see cref="McpEndpointUrl"/>.
    /// </summary>
    private string McpEndpointUrlOfThisServer() =>
        McpEndpointUrl.From(_server?.Features.Get<IServerAddressesFeature>()?.Addresses is { Count: > 0 } bound ? bound : _configuredUrls);

    /// <summary>
    /// Validates a project token and returns the project info if valid.
    /// Used by the MCP endpoint's project-token authentication.
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
}
