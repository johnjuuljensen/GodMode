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
using Microsoft.Extensions.Primitives;
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
    private readonly ClientSends _attentionSends = new();

    /// <summary>
    /// One subscribe at a time per connection, in the order they came: a connection's calls may run
    /// side by side (<c>MaximumParallelInvocationsPerClient</c>), and its replays would interleave.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _subscribeLocks = new();

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

    /// <summary>The server's configuration, where its roots and profiles are read from on every rebuild (<see cref="RootSources"/>).</summary>
    private readonly IConfiguration _configuration;

    /// <summary>The roots skipped for a clash, and the explicit roots whose folder is missing, by full path: each logged once while it lasts.</summary>
    private readonly HashSet<string> _loggedClashes = new(PathComparer);
    private readonly HashSet<string> _loggedMissingRoots = new(PathComparer);

    /// <summary>
    /// The generated API key's file, when the server has one (<see cref="AuthSettings.KeyFilePath"/>):
    /// no root source whose tree holds it is used. The start refuses one; this keeps out one added by a
    /// config reload later. Each left out is logged once, by folder, while it lasts.
    /// </summary>
    private readonly string? _keyFilePath;
    private readonly HashSet<string> _loggedKeyFileFolders = new(PathComparer);

    /// <summary>Which server this is, in the roots it holds and in the logs: <c>Instance</c>, <c>default</c> unless configured.</summary>
    public const string InstanceSetting = "Instance";
    public const string DefaultInstance = "default";
    private readonly string _instance;

    /// <summary>
    /// The roots this server manages, by full path: it holds each one's <see cref="RootLock"/>, from
    /// the rebuild that first found it free until the root is gone or the server stops. Only the
    /// snapshot's rebuild (under <see cref="_profileLock"/>) changes them.
    /// </summary>
    private readonly Dictionary<string, RootLock> _heldRoots = new(PathComparer);

    /// <summary>The roots another live server holds, by full path, each logged once while it is held.</summary>
    private readonly HashSet<string> _skippedRoots = new(PathComparer);

    /// <summary>Set once the server has stopped and let its roots go: it takes no root after that.</summary>
    private bool _rootsReleased;

    /// <summary>
    /// How often the roots are read again, so a root added, edited or removed on the host shows up
    /// without a reconnect, where a file watcher would miss it (a network drive). 0 turns the poll
    /// off; a reload of the config, and every list of roots or profiles, still read them.
    /// </summary>
    public const string RootsPollSetting = "RootsPollSeconds";
    private readonly TimeSpan _rootsPoll;

    /// <summary>
    /// One refresh of the roots at a time (<see cref="RefreshRootsAsync"/>), the startup's recovery
    /// included: what each changes (the fields below, the tracked sessions of roots that come and go)
    /// and what it pushes follow one another.
    /// </summary>
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    /// <summary>Set once the startup's recovery has run: from then on a refresh brings the tracked sessions in line with the roots.</summary>
    private bool _liveRoots;

    /// <summary>The roots whose sessions have been recovered, by full path, with the profile and name they were recovered under.</summary>
    private readonly Dictionary<string, (string Profile, string Root)> _recoveredRoots = new(PathComparer);

    /// <summary>
    /// The tracked sessions whose root's folder the last refresh found under another profile or name,
    /// or not at all ("" then), by ID: what it found. One refresh is not enough to act on: a
    /// config.json saved half-written reads as the default config, in another profile.
    /// </summary>
    private readonly Dictionary<string, string> _unbound = new();

    /// <summary>The roots and profiles the last refresh made, and what they serialize to, which says whether they changed.</summary>
    private sealed record RootsView(ProjectRootInfo[] Roots, ProfileInfo[] Profiles, string Json);
    private RootsView? _rootsView;

    /// <summary>What a refresh pushes, in order, not waited for (<see cref="ClientSends"/>).</summary>
    private readonly ClientSends _rootsSends = new();

    /// <summary>The poll and the config reload's subscription, started once the startup's recovery has run.</summary>
    private readonly CancellationTokenSource _watchStop = new();
    private IDisposable? _configReload;
    private int _watching;

    /// <summary>
    /// Lock for rebuilding the profile snapshot.
    /// </summary>
    private readonly object _profileLock = new();

    /// <summary>
    /// Immutable snapshot of all profile/root state. Swapped atomically via volatile.
    /// Readers capture the reference once to get a consistent view.
    /// </summary>
    private volatile ProfileSnapshot _snapshot;

    /// <summary>The roots the last snapshot logged, so it is logged only when they changed (under <see cref="_profileLock"/>).</summary>
    private string? _snapshotDescribed;

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
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        ILogger<ProjectManager> logger,
        IServer? server = null,
        AuthSettings? authSettings = null)
    {
        _lifecycle = lifecycle;
        _statusUpdater = statusUpdater;
        _rootConfigReader = rootConfigReader;
        _scriptRunner = scriptRunner;
        _hubContext = hubContext;
        _logger = logger;
        _server = server;
        _configuredUrls = (configuration["Urls"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _sessionStartTimeout = TimeSpan.FromSeconds(configuration.GetValue(SessionStartTimeoutSetting, 60.0));
        _statusScriptTimeout = TimeSpan.FromSeconds(configuration.GetValue(StatusScriptTimeoutSetting, 30.0));
        _exitBeforeShutdownWindow = TimeSpan.FromSeconds(configuration.GetValue(ExitBeforeShutdownWindowSetting, 5.0));
        _pullRequests = new PullRequestPoller(CheckPullRequestAsync,
            TimeSpan.FromSeconds(configuration.GetValue(PullRequestPollSetting, 600.0)), logger);
        _rootsPoll = TimeSpan.FromSeconds(Math.Max(0, configuration.GetValue(RootsPollSetting, 5.0)));
        _lifecycle.StatusNotified += OnStatusNotifiedAsync;

        _instance = configuration[InstanceSetting] is { Length: > 0 } instance ? instance : DefaultInstance;
        _logger.LogInformation("Server instance {Instance}", _instance);

        _configuration = configuration;
        _keyFilePath = authSettings?.KeyFilePath is { } keyFile ? Path.GetFullPath(keyFile) : null;
        foreach (var (setting, folder) in RootSources.From(configuration).Folders)
            _logger.LogInformation("Roots from {Setting}: {Folder}", setting, folder);
        foreach (var retired in RootSources.RetiredSettings(configuration))
            _logger.LogWarning("{Retired}", retired);

        // Build initial profile/root snapshot, taking the roots no other server holds
        lock (_profileLock) _snapshot = BuildSnapshot();

        _stopping = lifetime.ApplicationStopping;
        // Latest registration first: the roots are no longer watched, then the projects are
        // stopped, then the roots are let go
        lifetime.ApplicationStopping.Register(ReleaseRoots);
        lifetime.ApplicationStopping.Register(StopProjectsOnShutdown);
        lifetime.ApplicationStopping.Register(StopWatchingRoots);
    }

    /// <summary>
    /// Server shutdown: stops every claude as a Stop does, all at once (interrupted, then its process
    /// tree killed if it has not exited within the grace period, shortened to leave time for that),
    /// and persists Stopped, so recovery on the next start does not launch a second process on a
    /// session an orphan still runs. A project that was working or waiting on the user keeps that in
    /// <see cref="ProjectStatus.StateAtShutdown"/>, persisted before it is stopped, and the next
    /// start resumes it (<see cref="ResumeInterruptedProjectsAsync"/>); one a stop by the user was
    /// stopping is not marked. A process that exits on its own now counts as stopped, one handled
    /// just before is taken back (a server whose sessions share its console once lost them to its
    /// terminal's Ctrl+C), and its project is stopped like the rest, so the exit is persisted before
    /// the server goes. Blocks shutdown until done or <see cref="ShutdownTimeout"/> passes.
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
            .Select(project => (Project: project, Marker: ProjectLifecycle.MarkerOf(project)))
            .ToArray();
        var exited = _projects.Values.Except(running.Select(r => r.Project)).ToArray();
        if (running.Length == 0 && exited.Length == 0) return;

        _logger.LogInformation("Server stopping: stopping {Count} running project(s)", running.Length);
        var grace = TimeSpan.FromTicks(Math.Min(_lifecycle.StopGracePeriod.Ticks, (ShutdownTimeout - ShutdownKillMargin).Ticks));
        var stops = Task.WhenAll(
            running.Select(r => StopOnShutdownAsync(r.Project, async () =>
            {
                await _lifecycle.MarkForShutdownAsync(r.Project, r.Marker);
                await _lifecycle.StopAsync(r.Project, r.Marker, grace);
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
    /// Builds an immutable snapshot of all profile/root state: the roots its config names
    /// (<see cref="RootSources"/>), read fresh, grouped by profile, each profile with its settings.
    /// Thread-safe — can be called from any thread.
    /// </summary>
    private ProfileSnapshot BuildSnapshot()
    {
        var sources = RootSources.From(_configuration);
        var merged = new Dictionary<string, ProfileConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in FindRoots(sources))
        {
            if (!merged.TryGetValue(root.Profile, out var profile))
            {
                sources.Profiles.TryGetValue(root.Profile, out var settings);
                merged[root.Profile] = profile = new ProfileConfig
                {
                    Roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    Environment = settings is { Environment.Count: > 0 } ? new Dictionary<string, string>(settings.Environment) : null,
                    Description = settings?.Description ?? root.Description,
                };
            }
            profile.Roots[root.Name] = root.Path;
        }

        // One server per root: before the default, so a server all of whose roots are held elsewhere has what a server with no roots has
        HoldRoots(merged);

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

        // Said when it changed, not on every rebuild: the poll rebuilds every few seconds
        var described = string.Join(", ", rootLookup.Select(kvp => $"{kvp.Key.Item1}/{kvp.Key.Item2}={kvp.Value}"));
        if (described != _snapshotDescribed)
        {
            _snapshotDescribed = described;
            _logger.LogInformation("Profile snapshot: {ProfileCount} profiles, {RootCount} total roots: {Roots}",
                merged.Count, rootLookup.Count, described);
        }

        return new ProfileSnapshot(merged, rootLookup, pathToProfileRoot, projectFiles);
    }

    /// <summary>
    /// One server per root: removes from <paramref name="profiles"/> every root another live server
    /// holds, and takes the lock on each one that is free. A root held elsewhere is logged once, with
    /// its holder where that can be read, and tried again on every rebuild; a root this server holds
    /// stays held across rebuilds, and is let go once it is no longer found and no project of this
    /// server's is in it (one being created included) until the server stops. A root whose folder does
    /// not exist holds nothing yet, and is kept. A profile left with no root by this is not listed either.
    /// </summary>
    private void HoldRoots(Dictionary<string, ProfileConfig> profiles)
    {
        var found = new HashSet<string>(PathComparer);
        foreach (var (profileName, config) in profiles.ToArray())
        {
            var hadRoots = config.Roots.Count > 0;
            foreach (var (rootName, rootPath) in config.Roots.ToArray())
            {
                var path = FullPath(rootPath);
                if (!Directory.Exists(path)) continue;
                found.Add(path);
                if (_heldRoots.ContainsKey(path)) continue;
                if (!_rootsReleased && TryHoldRoot(path) is { } held)
                {
                    _heldRoots[path] = held;
                    if (_skippedRoots.Remove(path))
                        _logger.LogInformation("Root {Profile}/{Root} at {Path} is free again: this server ({Instance}) now holds it",
                            profileName, rootName, path, _instance);
                    continue;
                }

                config.Roots.Remove(rootName);
                if (!_rootsReleased && _skippedRoots.Add(path))
                {
                    var holder = RootLock.ReadHolder(path);
                    _logger.LogWarning("Root {Profile}/{Root} at {Path} is held by another server ({Holder}): skipped until it is let go",
                        profileName, rootName, path, holder is null ? "unknown" : $"instance {holder.Instance}, process {holder.ProcessId}");
                }
            }
            if (hadRoots && config.Roots.Count == 0) profiles.Remove(profileName);
        }

        // Not found by one rebuild is not gone: a config.json saved mid-edit, or a folder that blinks.
        // A root with a project of this server's in it stays held, so no other server takes its sessions
        foreach (var gone in _heldRoots.Keys.Where(path => !found.Contains(path) && !HasProjectIn(path)).ToArray())
        {
            _heldRoots.Remove(gone, out var held);
            held!.Dispose();
        }
        _skippedRoots.IntersectWith(found);
    }

    /// <summary>Whether a project this server tracks, or one it is creating, is in the root at <paramref name="rootPath"/>.</summary>
    private bool HasProjectIn(string rootPath) =>
        _projects.Values.Select(project => project.ProjectPath).Concat(CreatingPaths())
            .Any(path => WhyNotAProjectFolderOf(rootPath, path) is null);

    /// <summary>The folders creates in progress have claimed, copied under their lock.</summary>
    private string[] CreatingPaths()
    {
        lock (_creatingPathsLock) return [.. _creatingPaths.Keys];
    }

    /// <summary>The lock on the root at <paramref name="path"/>, or null when another server holds it or it cannot be taken.</summary>
    private RootLock? TryHoldRoot(string path)
    {
        try
        {
            return RootLock.TryAcquire(path, _instance);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not take the lock on root {Path}", path);
            return null;
        }
    }

    /// <summary>Lets every root go, once the server has stopped its projects: another server may take them from here.</summary>
    private void ReleaseRoots()
    {
        lock (_profileLock)
        {
            _rootsReleased = true;
            foreach (var held in _heldRoots.Values) held.Dispose();
            _heldRoots.Clear();
        }
    }

    /// <summary>Rebuilds the profile snapshot, and returns it.</summary>
    private ProfileSnapshot RebuildSnapshot()
    {
        lock (_profileLock) return _snapshot = BuildSnapshot();
    }

    /// <summary>A root as its source found it, with the profile it goes in.</summary>
    private sealed record FoundRoot(string Name, string Path, string Profile, string? Description, string Source);

    /// <summary>
    /// The roots <paramref name="sources"/> name: the explicit ones, then each scan folder's, in ordinal
    /// order of their keys. One name, one root per server, and one folder, one root: an explicit root
    /// wins a clash, and between scan folders the first key does. Each loser is logged once, with both
    /// paths, while the clash lasts. A root's profile is its config.json's <c>profileName</c>, else its
    /// explicit entry's <c>Profile</c>, else <c>Default</c>.
    /// </summary>
    private List<FoundRoot> FindRoots(RootSources sources)
    {
        var roots = new List<FoundRoot>();
        var byName = new Dictionary<string, FoundRoot>(StringComparer.OrdinalIgnoreCase);
        var byPath = new Dictionary<string, FoundRoot>(PathComparer);
        var clashes = new HashSet<string>(PathComparer);
        var holdingTheKey = new HashSet<string>(PathComparer);

        // Where sessions work, the key file never is: a source added since the start that holds it is left out
        bool HoldsTheKeyFile(string folder, string setting)
        {
            if (_keyFilePath is null || !ApiKeyFile.IsUnder(folder, _keyFilePath)) return false;
            holdingTheKey.Add(folder);
            if (_loggedKeyFileFolders.Add(folder))
                _logger.LogWarning("{Setting} ({Folder}) is left out: the server's API key file, {KeyFile}, is in its tree, where sessions would work. " +
                    "Name a folder without it, or move the key file (Authentication:ApiKeyFile)", setting, folder, _keyFilePath);
            return true;
        }

        void Add(string name, string path, string? entryProfile, string source)
        {
            // The same folder found again under the same name (an explicit root in a scan folder) is that root
            if (byPath.TryGetValue(path, out var samePath) && string.Equals(samePath.Name, name, StringComparison.OrdinalIgnoreCase))
                return;
            if ((byName.TryGetValue(name, out var winner) ? winner : byPath.GetValueOrDefault(path)) is { } taken)
            {
                clashes.Add(path);
                if (_loggedClashes.Add(path))
                    _logger.LogWarning("Root {Name} at {Path} ({Source}) is skipped: it clashes with the root {Winner} at {WinnerPath} ({WinnerSource}), " +
                        "which wins. A server has one root per name and per folder",
                        name, path, source, taken.Name, taken.Path, taken.Source);
                return;
            }
            try
            {
                var config = _rootConfigReader.ReadConfig(path);
                var root = new FoundRoot(name, path, config.ProfileName ?? entryProfile ?? "Default", config.Description, source);
                roots.Add(root);
                byName[name] = root;
                byPath[path] = root;
                _logger.LogDebug("Found root '{RootName}' → profile '{ProfileName}' at {Path} ({Source})", name, root.Profile, path, source);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read config for root at {Path}, skipping", path);
            }
        }

        foreach (var root in sources.ExplicitRoots)
        {
            var setting = $"{RootSources.ExplicitSection}:{root.Name}";
            if (HoldsTheKeyFile(root.Path, $"{setting}:Path")) continue;
            if (Directory.Exists(root.Path))
                Add(root.Name, root.Path, root.Profile, setting);
            else if (_loggedMissingRoots.Add(root.Path))
                _logger.LogWarning("Root {Name} at {Path} ({Setting}:Path) does not exist: skipped until it does", root.Name, root.Path, setting);
        }
        foreach (var scan in sources.ScanFolders)
        {
            var setting = $"{RootSources.ScanSection}:{scan.Key}";
            if (HoldsTheKeyFile(scan.Folder, setting)) continue;
            if (!Directory.Exists(scan.Folder))
            {
                _logger.LogDebug("Scan folder {Folder} ({Setting}) does not exist, skipping it", scan.Folder, setting);
                continue;
            }
            foreach (var subDir in Directory.GetDirectories(scan.Folder).Order(StringComparer.Ordinal))
                if (Directory.Exists(Path.Combine(subDir, ProjectFiles.ProjectFolder.RootConfigFolderName)))
                    Add(Path.GetFileName(subDir), FullPath(subDir), null, setting);
        }

        _loggedClashes.IntersectWith(clashes);
        _loggedKeyFileFolders.IntersectWith(holdingTheKey);
        _loggedMissingRoots.IntersectWith(sources.ExplicitRoots.Select(root => root.Path).Where(path => !Directory.Exists(path)));
        return roots;
    }

    private static (Dictionary<(string, string), string>, Dictionary<string, (string, string)>) BuildRootLookups(
        Dictionary<string, ProfileConfig> profiles)
    {
        var rootLookup = new Dictionary<(string, string), string>(TupleComparer.Instance);
        var pathLookup = new Dictionary<string, (string, string)>(PathComparer);

        foreach (var (profileName, config) in profiles)
        {
            foreach (var (rootName, rootPath) in config.Roots)
            {
                rootLookup[(profileName, rootName)] = rootPath;
                pathLookup[FullPath(rootPath)] = (profileName, rootName);
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
    /// A session's opaque ID: <c>{profile}/{root}/{id}</c>, its id being unique within its root, so one
    /// id in two roots is two sessions. Clients treat it as opaque; it is never parsed. It is derived again
    /// from where its state folder is on every recovery, not trusted from status.json.
    /// </summary>
    private static string ProjectId(string profile, string root, string sessionId) => $"{CompositeKey(profile, root)}/{sessionId}";

    /// <summary>Every root with its profile and root names as configured, and its full path.</summary>
    private static IEnumerable<(string Profile, string Root, string Path)> AllRoots(ProfileSnapshot snap) =>
        snap.RootLookup.Keys.Select(key => (key.Item1, key.Item2, snap.ProjectFiles.GetProjectRootPath(CompositeKey(key.Item1, key.Item2))));

    /// <summary>The profile and root names as configured, for names a client may have cased differently.</summary>
    private static (string Profile, string Root) ConfiguredNames(ProfileSnapshot snap, string profile, string root) =>
        snap.RootLookup.Keys.FirstOrDefault(key => TupleComparer.Instance.Equals(key, (profile, root))) is ({ } p, { } r) ? (p, r) : (profile, root);

    /// <summary>
    /// Why <paramref name="path"/> cannot be a project folder of the root at <paramref name="rootPath"/>,
    /// or null when it can. It must be strictly under the root, links followed on both where the OS
    /// allows (a link in the root to a folder elsewhere is that folder), and its first folder under the
    /// root must be no folder the root keeps for itself (<c>{root}/.godmode-root/scripts</c> is the root's).
    /// A delete of the project deletes its folder recursively.
    /// </summary>
    private static string? WhyNotAProjectFolderOf(string rootPath, string path)
    {
        var relative = Path.GetRelativePath(ResolveLinks(rootPath), ResolveLinks(path));
        if (relative == "." || Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            return "is not inside its project root";
        return ProjectFiles.ProjectFolder.IsReservedFolderName(relative.Split(Path.DirectorySeparatorChar)[0])
            ? "is inside a folder the project root uses for itself"
            : null;
    }

    /// <summary>
    /// The full path of <paramref name="path"/> with every link along it followed, as far as it exists
    /// and the OS lets it be read; the rest as it is.
    /// </summary>
    private static string ResolveLinks(string path, int depth = 0)
    {
        var full = FullPath(path);
        var resolved = Path.GetPathRoot(full)!;
        foreach (var segment in full[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, segment);
            try
            {
                // The target's own folders may be links too; a cycle ends at the depth, as the OS's does
                if (depth < 32 && new DirectoryInfo(resolved) is { Exists: true, LinkTarget: not null } link
                    && link.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    resolved = ResolveLinks(target.FullName, depth + 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return resolved;
    }

    // Each reads the roots again, to pick up roots added on the host or in config, as a poll does
    public async Task<ProfileInfo[]> ListProfilesAsync() => (await RefreshRootsAsync()).Profiles;

    public async Task<ProjectRootInfo[]> ListProjectRootsAsync() => (await RefreshRootsAsync()).Roots;

    /// <summary>The roots and profiles of <paramref name="snap"/> as the hub lists them, each root with its actions read fresh.</summary>
    private RootsView BuildRootsView(ProfileSnapshot snap)
    {
        var profiles = snap.Profiles.Select(kvp => new ProfileInfo(kvp.Key, kvp.Value.Description)).ToArray();
        var roots = AllRoots(snap).Select(root =>
        {
            var config = _rootConfigReader.ReadConfig(root.Path);
            var actions = config.GetEffectiveActions()
                .Select(a => new CreateActionInfo(a.Name, a.Description, a.InputSchema, a.Model, a.AllowSkipPermissions))
                .ToArray();
            return new ProjectRootInfo(root.Root, config.Description, actions, ProfileName: root.Profile);
        }).ToArray();
        return new RootsView(roots, profiles, JsonSerializer.Serialize(new { roots, profiles }, JsonDefaults.Options));
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
                PullRequest: s.PullRequest,
                Kind: s.Kind
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
        // A shared folder is used as it is: that is what sharing it means
        var suffixed = !action.ScriptsCreateFolder && !action.SharedFolder && !reuseExisting && autoSuffix && Directory.Exists(Path.Combine(rootPath, folder));
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

        // The session's id, yymmdd-{kind}-{slug}-{suffix}, unique within its root. Until the create
        // script has run its kind is the action's name, and its slug the name so far: the script's
        // result may name another of either, and the date and suffix stay
        var createdOn = DateTime.Now;
        var kind = ProjectFiles.SessionState.Kind(action.Name);
        var sessionId = FreeSessionId(snap, compositeKey, profileName, rootName,
            suffix => ProjectFiles.SessionState.Id(createdOn, kind, name, suffix));
        var projectId = ProjectId(profileName, rootName, sessionId);

        // One project per ID, and per folder unless its action shares folders: a tracked project's
        // claude would be orphaned, and its files overwritten, and a delete of either would remove
        // the other's. Claimed before a folder is reused or any script runs, until registered
        using var claims = new CreateClaims(this, action.SharedFolder);
        claims.Claim(projectId, projectPath);

        // Unless the scripts create the project directory (e.g. git worktree add). Its session's
        // state is made once the scripts have run, when its id is final
        if (!action.ScriptsCreateFolder)
        {
            if (reuseExisting || (action.SharedFolder && Directory.Exists(projectPath)))
                ProjectFiles.ProjectFolder.Reuse(rootPath, folder);
            else
                ProjectFiles.ProjectFolder.Create(rootPath, folder);
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
                ProfileName: profileName,
                Kind: kind
            ),
            ProjectPath = projectPath,
            RootPath = FullPath(rootPath),
            SessionId = sessionId,
            ActionName = action.Name,
            ProfileName = profileName,
            SharedFolder = action.SharedFolder,
        };

        // Result file — scripts can write key=value pairs to override project path/name. It and the
        // script log are the session's, by its id: sessions that share a folder have one each
        var resultFilePath = GetResultFilePath(rootPath, sessionId);
        if (File.Exists(resultFilePath)) File.Delete(resultFilePath);

        // Build environment variables for scripts (profile env merged in)
        var scriptEnv = BuildScriptEnvironment(rootPath, project, action, request.Inputs, profileEnv, resultFilePath,
            request.ProfileName, config.StripEnvVarProfile);

        // Script log file — at root level so it persists regardless of what scripts do
        var logFilePath = GetScriptLogPath(rootPath, sessionId);

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

        // Apply script result overrides (project_path, project_name, kind)
        var scriptResults = ReadResultFile(resultFilePath);
        if (scriptResults.TryGetValue("project_path", out var overridePath) && !string.IsNullOrWhiteSpace(overridePath))
        {
            try
            {
                (projectPath, folder) = ValidateScriptProjectPath(overridePath, rootPath);
                // Nor may a script's folder be a tracked project's
                claims.Claim(projectId, projectPath);
            }
            catch (Exception ex) when (ex is ArgumentException or ProjectInUseException)
            {
                // The project keeps the folder it was given, so a delete removes only that
                _logger.LogError("Create script for project {ProjectId} returned a project_path it cannot have: {Message}", projectId, ex.Message);
                RegisterFailedCreate(project, ex.Message);
                throw;
            }
            project.ProjectPath = projectPath;
            _logger.LogInformation("Script overrode project path to {ProjectPath}", projectPath);
        }
        if (scriptResults.TryGetValue("project_name", out var overrideName) && !string.IsNullOrWhiteSpace(overrideName))
        {
            name = overrideName;
            _logger.LogInformation("Script overrode project name to '{ProjectName}'", name);
        }
        if (scriptResults.TryGetValue("kind", out var scriptKind) && !string.IsNullOrWhiteSpace(scriptKind))
        {
            kind = ProjectFiles.SessionState.Kind(scriptKind);
            _logger.LogInformation("Script named the session's kind '{Kind}'", kind);
        }

        // The id as the script's result leaves it: its kind and name, with the date and suffix it had
        var finalId = ProjectFiles.SessionState.Id(createdOn, kind, name, sessionId[^ProjectFiles.SessionState.SuffixLength..]);
        if (finalId != sessionId)
        {
            try
            {
                finalId = FreeSessionId(snap, compositeKey, profileName, rootName,
                    suffix => ProjectFiles.SessionState.Id(createdOn, kind, name, suffix), finalId);
                claims.Claim(ProjectId(profileName, rootName, finalId), projectPath);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ProjectInUseException)
            {
                _logger.LogError("Project {ProjectId} could not have the id {SessionId}: {Message}", projectId, finalId, ex.Message);
                RegisterFailedCreate(project, ex.Message);
                throw;
            }
            // The log and result file follow the id, so they are found by the id the session keeps.
            // Replacing is safe: the final id is free (FreeSessionId) and claimed above, so no other
            // create or session has files under it
            MoveScriptFile(logFilePath, GetScriptLogPath(rootPath, finalId));
            MoveScriptFile(resultFilePath, GetResultFilePath(rootPath, finalId));
            sessionId = finalId;
            projectId = ProjectId(profileName, rootName, sessionId);
            project.SessionId = sessionId;
            _logger.LogInformation("The session's id is {ProjectId}", projectId);
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
        project.Status = project.Status with { Id = projectId, Name = name, Model = model, Kind = kind };

        // The session's state folder, now its id is final (scripts may have created the project dir without .godmode)
        CreateSessionState(project);

        // Save project settings (persists across restarts, includes action name for delete/resume).
        // The permission mode is kept with the project, as its model is, so its resumes keep it
        var settings = new ProjectFiles.ProjectSettings(
            DangerouslySkipPermissions: skipPermissions,
            ActionName: action.Name,
            PermissionMode: action.PermissionMode,
            SharedFolder: action.SharedFolder);
        settings.Save(project.StatePath);

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

    /// <summary>
    /// The IDs and folders (full paths) that creates in progress have claimed: see <see cref="CreateClaims"/>.
    /// A folder's claim says whether it is shared, and how many creates share it; it is changed under <see cref="_creatingPathsLock"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _creatingIds = new();
    private readonly Dictionary<string, (bool Shared, int Creates)> _creatingPaths = new(PathComparer);
    private readonly Lock _creatingPathsLock = new();

    /// <summary>Paths compared as the OS compares them: on Windows, <c>Fix</c> is the folder <c>fix</c>.</summary>
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string FullPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// What one create has claimed, so no two creates make one project and none makes a tracked one:
    /// from before anything is written until the project is registered or the create has failed.
    /// The ID is always the create's alone. The folder is too, unless the create's action shares
    /// folders (<paramref name="shared"/>): then other creates and sessions may have it, as long as
    /// they all share it. Disposing it gives the claims up.
    /// </summary>
    private sealed class CreateClaims(ProjectManager manager, bool shared) : IDisposable
    {
        private readonly List<string> _ids = [];
        private readonly List<string> _paths = [];

        /// <summary>
        /// Claims the ID and folder, or throws <see cref="ProjectInUseException"/> when a tracked
        /// project has the ID, or another create has claimed it, or the folder is in use
        /// (<see cref="WhyFolderIsInUse"/>).
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
                lock (manager._creatingPathsLock)
                {
                    var claimed = manager._creatingPaths.TryGetValue(path, out var claim);
                    if (claimed && !(shared && claim.Shared))
                        throw new ProjectInUseException(projectId, $"another create is making {path}");
                    manager._creatingPaths[path] = (shared, claimed ? claim.Creates + 1 : 1);
                }
                _paths.Add(path);
            }
            if (manager._projects.ContainsKey(projectId))
                throw new ProjectInUseException(projectId, "a project with this ID exists");
            if (manager.WhyFolderIsInUse(path, shared) is { } reason)
                throw new ProjectInUseException(projectId, reason);
        }

        public void Dispose()
        {
            foreach (var id in _ids) manager._creatingIds.TryRemove(id, out _);
            lock (manager._creatingPathsLock)
            {
                foreach (var path in _paths)
                {
                    if (manager._creatingPaths.TryGetValue(path, out var claim) && claim.Creates > 1)
                        manager._creatingPaths[path] = claim with { Creates = claim.Creates - 1 };
                    else
                        manager._creatingPaths.Remove(path);
                }
            }
            _ids.Clear();
            _paths.Clear();
        }
    }

    /// <summary>
    /// Why a new session cannot have the working folder <paramref name="path"/> (a full path), or null
    /// when it can. A session that does not share its folder needs one no other session has, tracked
    /// or only on disk in its <c>.godmode/sessions/</c>: its delete removes the folder. One that shares
    /// it (<paramref name="shared"/>) may join sessions that share it too, and no other kind.
    /// </summary>
    private string? WhyFolderIsInUse(string path, bool shared)
    {
        var tracked = _projects.Values.Where(p => PathComparer.Equals(FullPath(p.ProjectPath), path)).ToArray();
        if (tracked.FirstOrDefault(p => !shared || !p.SharedFolder) is { } holder)
            return holder.SharedFolder
                ? $"project {holder.Status.Id} is in {path}, which its sessions share, and this create's action does not share folders (sharedFolder)"
                : $"project {holder.Status.Id} is in {path}";

        // A session's state left on disk and not tracked (not recovered) is the folder's too
        var untracked = ProjectFiles.SessionState.List(path).Where(id => !tracked.Any(p => p.SessionId == id));
        return untracked.FirstOrDefault(id => !shared || !ProjectFiles.ProjectSettings.Load(ProjectFiles.SessionState.PathOf(path, id)).SharedFolder) is { } other
            ? $"session {other} has its state in {path}"
            : null;
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
        var reply = await WithTrackedLockAsync(project, () => ReplyAndResumeLockedAsync(project, text, onlyIfInterrupted: false));
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

    /// <summary>
    /// <see cref="WithResumeLockAsync{T}"/> for a call on a project it looked up by its ID: once the lock is held, the
    /// project must still be the one tracked under that ID, or the call fails as for a project not found. A refresh may
    /// have let it go while the call waited (<see cref="TryForgetAsync"/>, which takes the lock too), and a launch then
    /// would run a claude that nothing tracks, stops at shutdown or holds its root for.
    /// </summary>
    private Task<T> WithTrackedLockAsync<T>(ProjectInfo project, Func<Task<T>> action) =>
        WithResumeLockAsync(project, () => IsTracked(project)
            ? action()
            : throw new KeyNotFoundException($"Project {project.Status.Id} not found: it left the list while this waited"));

    private Task WithTrackedLockAsync(ProjectInfo project, Func<Task> action) =>
        WithTrackedLockAsync(project, async () => { await action(); return true; });

    /// <summary>Whether <paramref name="project"/> is the one tracked under its ID.</summary>
    private bool IsTracked(ProjectInfo project) =>
        _projects.TryGetValue(project.Status.Id, out var tracked) && ReferenceEquals(tracked, project);

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
            var rootPath = project.RootPath;
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

    public async ValueTask DisposeAsync()
    {
        StopWatchingRoots();
        await _pullRequests.DisposeAsync();
        ReleaseRoots();
    }

    public void Dispose()
    {
        StopWatchingRoots();
        _pullRequests.Dispose();
        ReleaseRoots();
    }

    /// <summary>
    /// Pushes the attention list to every client when it differs from the one last pushed. The list
    /// is computed and its push started under the lock, so lists go out in the order they were
    /// computed; the push is not waited for (<see cref="ClientSends"/>), so a client that does not
    /// read holds up neither the lock nor the project whose status push got here.
    /// </summary>
    private async Task PushAttentionIfChangedAsync()
    {
        Task window;
        await _attentionLock.WaitAsync();
        try
        {
            var attention = GetAttention();
            if (Attention.Same(attention, _attention)) return;
            _attention = attention;
            window = _attentionSends.SendAsync(() => _hubContext.Clients.All.AttentionChanged(attention),
                ex => _logger.LogError(ex, "Error pushing the attention list"));
        }
        finally
        {
            _attentionLock.Release();
        }
        await window;
    }

    public async Task RespondToPermissionAsync(string projectId, string requestId, PermissionDecision decision)
    {
        var (project, pending) = FindPending(projectId, requestId);
        if (decision.Allow && pending.Question != null)
            throw new InvalidOperationException($"Request {requestId} is a question: answer it with AnswerQuestion");

        var result = decision.Allow
            ? PermissionPromptResult.Allow(decision.UpdatedInput ?? pending.Input)
            : PermissionPromptResult.Deny(decision.Message is { Length: > 0 } message ? message : "The user denied this.");
        await AnswerPendingAsync(project, pending, result);
        _logger.LogInformation("Project {ProjectId}: permission request {RequestId} {Decision}",
            projectId, requestId, decision.Allow ? "allowed" : "denied");
    }

    public Task<PermissionDetail> GetPermissionDetailAsync(string projectId, string requestId)
    {
        var (_, pending) = FindPending(projectId, requestId);
        return pending.Detail is { } detail
            ? Task.FromResult(detail)
            : throw new InvalidOperationException($"Request {requestId} is a question: it has no permission detail");
    }

    public async Task AnswerQuestionAsync(string projectId, string requestId, IReadOnlyDictionary<string, string> answers)
    {
        var (project, pending) = FindPending(projectId, requestId);
        if (pending.Question == null)
            throw new InvalidOperationException($"Request {requestId} is not a question: answer it with RespondToPermission");
        if (answers.Count == 0)
            throw new ArgumentException("An answer needs at least one question answered", nameof(answers));

        await AnswerPendingAsync(project, pending, PermissionPromptResult.Allow(PermissionPrompts.WithAnswers(pending.Input, answers)));
        _logger.LogInformation("Project {ProjectId}: question {RequestId} answered", projectId, requestId);
    }

    public async Task<PermissionPromptResult> RequestPermissionAsync(string projectId, PermissionPromptRequest request, CancellationToken aborted)
    {
        if (!_projects.TryGetValue(projectId, out var project))
            throw new KeyNotFoundException($"Project {projectId} not found");

        var pending = PermissionPrompts.Create(request, project.ProjectPath, DateTime.UtcNow);
        // Tool name only: the input and its summary can carry secrets (a command with a token in it)
        _logger.LogInformation("Project {ProjectId} asks permission for {ToolName} (request {RequestId})",
            projectId, request.ToolName, pending.Id);

        try
        {
            project.Process.AddPending(pending);
            await _lifecycle.ShowPendingAsync(project);
            return await pending.Completion.Task.WaitAsync(aborted);
        }
        catch (OperationCanceledException)
        {
            // claude cancelled the call, or its connection dropped (it exited or was killed): nobody is waiting for the answer
            _logger.LogInformation("Project {ProjectId}: permission request {RequestId} was abandoned", projectId, pending.Id);
            await CompletePendingAsync(project, pending, PermissionPromptResult.Deny("The request was abandoned."));
            throw;
        }
        catch (Exception ex)
        {
            // Not shown: claude's call fails, and no reply is taken for its answer
            _logger.LogError(ex, "Project {ProjectId}: permission request {RequestId} could not be shown; it is withdrawn", projectId, pending.Id);
            await CompletePendingAsync(project, pending, PermissionPromptResult.Deny("The request could not be shown to the user."));
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

    /// <summary>Answers the request, if nothing else did first, and shows the next one or none; whether this call answered it.</summary>
    private async Task<bool> CompletePendingAsync(ProjectInfo project, PendingRequest pending, PermissionPromptResult result)
    {
        if (!project.Process.CompletePending(pending, result)) return false;
        await _lifecycle.ShowPendingAsync(project);
        return true;
    }

    /// <summary>
    /// The user's answer: it fails when another answer (another client's, a chat reply) or claude's
    /// giving up came first, since that is what claude got, not this.
    /// </summary>
    private async Task AnswerPendingAsync(ProjectInfo project, PendingRequest pending, PermissionPromptResult result)
    {
        if (!await CompletePendingAsync(project, pending, result))
            throw new KeyNotFoundException(
                $"Request {pending.Id} was answered already, by another client or a reply, or claude stopped waiting: this answer was not used");
    }

    public async Task StopProjectAsync(string projectId)
    {
        if (!_projects.TryGetValue(projectId, out var project))
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        // Before a launch or after it, never in the middle of one
        await WithTrackedLockAsync(project, () => _lifecycle.StopAsync(project));
        await NotifyStatusChanged(project);
    }

    public async Task DeleteProjectAsync(string projectId, bool force = false)
    {
        if (!_projects.TryGetValue(projectId, out var project))
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        await WithTrackedLockAsync(project, () => DeleteLockedAsync(project, force));
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
        var sharedFolder = project.SharedFolder || OthersInFolder(project);
        try
        {
            if (project.Status.RootName != null && profileName != null)
            {
                var rootPath = project.RootPath;
                var config = _rootConfigReader.ReadConfig(rootPath);
                var action = config.ResolveAction(project.ActionName);
                // An action that shares folders now shares this one too, whatever the session was created as
                sharedFolder |= action?.SharedFolder == true;

                if (action?.Delete is { Length: > 0 })
                {
                    snap.Profiles.TryGetValue(profileName, out var profileCfg);
                    var scriptEnv = BuildScriptEnvironment(rootPath, project, action, new Dictionary<string, JsonElement>(), profileCfg?.Environment,
                        profileName: profileName, stripEnvVarProfile: config.StripEnvVarProfile);

                    if (force)
                        scriptEnv["GODMODE_FORCE"] = "true";
                    // A shared folder stays: the script leaves it, and whatever the other sessions use, alone
                    scriptEnv[SharedFolderVariable] = sharedFolder ? "true" : "false";

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

        // A session that shares its folder takes only its own state with it. Otherwise the folder is
        // its own: use robust deletion to handle locked/read-only files (common with .git directories
        // on Windows after git init or process shutdown)
        if (sharedFolder)
            await RemoveSessionStateAsync(project);
        else
            await DeleteDirectoryRobustAsync(project.ProjectPath, project.RootPath);

        _logger.LogInformation("Project {ProjectId} deleted successfully{Kept}", projectId,
            sharedFolder ? $"; its working folder {project.ProjectPath} is shared, and stays" : "");
        await PushAttentionIfChangedAsync();
    }

    public async Task ResumeProjectAsync(string projectId)
    {
        if (!_projects.TryGetValue(projectId, out var project))
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        await WithTrackedLockAsync(project, async () =>
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
            // The claim failed after it was made: nothing is launched, and no launch is left in
            // flight for the next claim, stop or reply to wait on. A save that fails is not this:
            // the claim stands, and is saved with the next change
            if (claimed) project.Process.EndLaunching();
            throw;
        }
        if (!claimed) return false;

        _logger.LogInformation("Resuming project {ProjectId} with session {SessionId}", projectId, project.ClaudeSessionId);
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
        var subscribeLock = _subscribeLocks.GetOrAdd(connectionId, _ => new SemaphoreSlim(1, 1));
        await subscribeLock.WaitAsync();
        try { await _lifecycle.SubscribeAsync(project, fromOffset, subscriptionId, generation, connectionId); }
        finally { subscribeLock.Release(); }
    }

    /// <summary>
    /// Takes the connection out of the project's live group, after any subscribe it made before:
    /// a subscribe still replaying would otherwise put it back in the group once it is done.
    /// </summary>
    public async Task UnsubscribeProjectAsync(string projectId, string connectionId)
    {
        var subscribeLock = _subscribeLocks.GetOrAdd(connectionId, _ => new SemaphoreSlim(1, 1));
        await subscribeLock.WaitAsync();
        try { await _hubContext.Groups.RemoveFromGroupAsync(connectionId, ProjectLifecycle.OutputGroup(projectId)); }
        finally { subscribeLock.Release(); }

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
        // A subscribe still running holds its own reference; the connection makes no more
        _subscribeLocks.TryRemove(connectionId, out _);
        await Task.CompletedTask;
    }

    /// <summary>What a script is told whether the session shares its working folder with, <c>true</c> or <c>false</c>.</summary>
    public const string SharedFolderVariable = "GODMODE_SHARED_FOLDER";

    private static readonly JsonSerializerOptions CaseInsensitiveOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task RecoverProjectsAsync()
    {
        await _refreshLock.WaitAsync();
        try
        {
            // Rebuild to pick up roots added on the host or in config
            var snap = RebuildSnapshot();
            _logger.LogInformation("Recovering projects from all project roots");
            var roots = RootsOf(snap);
            await RecoverRootsAsync(snap, roots);
            foreach (var root in roots) _recoveredRoots[root.Path] = (root.Profile, root.Root);
            _liveRoots = true;
            await PublishRootsViewAsync(snap);
        }
        finally
        {
            _refreshLock.Release();
        }
        await PushAttentionIfChangedAsync();
        StartWatchingRoots();
    }

    /// <summary>Every root of <paramref name="snap"/>, its path full.</summary>
    private static (string Profile, string Root, string Path)[] RootsOf(ProfileSnapshot snap) =>
        AllRoots(snap).Select(root => (root.Profile, root.Root, FullPath(root.Path))).ToArray();

    // ── Live roots: a root added, edited or removed shows up without a restart or a reconnect ──

    /// <summary>
    /// Reads the roots again and brings what follows from them up to date: the root locks
    /// (<see cref="HoldRoots"/>), the lists clients hold (<see cref="IProjectHubClient.RootsChanged"/>,
    /// pushed only when they changed) and, once the startup's recovery has run, the tracked sessions
    /// (<see cref="ReconcileSessionsAsync"/>). Every list of roots or profiles, the poll
    /// (<see cref="RootsPollSetting"/>) and a reload of the config call it, one at a time.
    /// </summary>
    private async Task<RootsView> RefreshRootsAsync()
    {
        RootsView view;
        var sessionsChanged = false;
        await _refreshLock.WaitAsync();
        try
        {
            var snap = RebuildSnapshot();
            view = await PublishRootsViewAsync(snap);
            if (_liveRoots && !_lifecycle.ShuttingDown) sessionsChanged = await ReconcileSessionsAsync(snap);
        }
        finally
        {
            _refreshLock.Release();
        }
        if (sessionsChanged) await PushAttentionIfChangedAsync();
        return view;
    }

    /// <summary>The lists of <paramref name="snap"/>, pushed to every client when they differ from the last ones made (the first ones are listed, not pushed).</summary>
    private async Task<RootsView> PublishRootsViewAsync(ProfileSnapshot snap)
    {
        var view = BuildRootsView(snap);
        var last = _rootsView;
        _rootsView = view;
        if (last != null && last.Json != view.Json)
        {
            _logger.LogInformation("The roots changed: {Roots}", string.Join(", ", view.Roots.Select(root => $"{root.ProfileName}/{root.Name}")));
            await PushAsync(() => _hubContext.Clients.All.RootsChanged(view.Roots, view.Profiles), "the roots");
        }
        return view;
    }

    /// <summary>A push of a refresh, in order with its others, and not waited for.</summary>
    private Task PushAsync(Func<Task> push, string what) =>
        _rootsSends.SendAsync(push, ex => _logger.LogError(ex, "Error pushing {What}", what));

    /// <summary>
    /// Brings the tracked sessions in line with the roots of <paramref name="snap"/>. A session is its
    /// root's by folder (<see cref="ProjectInfo.RootPath"/>). When two refreshes in a row find that
    /// folder under another profile or name (its <c>profileName</c> edited, its explicit entry renamed),
    /// or not at all (the root removed, or beaten by a new root of its name), a session without a claude
    /// is no longer tracked, its files left as they are, and, when its folder is still a root, it is
    /// recovered again under the ID it has there: <c>{profile}/{root}/{id}</c> names both. One whose
    /// claude runs or launches carries on under its ID until claude exits, and its root's config and
    /// scripts are read from its folder all along. Then every root whose sessions were not recovered
    /// under its profile and name (one that appeared) has them recovered, as at the start. Each session
    /// that goes is pushed as ProjectDeleted, and each that comes as ProjectCreated. Says whether any did.
    /// </summary>
    private async Task<bool> ReconcileSessionsAsync(ProfileSnapshot snap)
    {
        var gone = new List<string>();
        var again = new HashSet<string>(PathComparer);
        foreach (var project in _projects.Values.ToArray())
        {
            var id = project.Status.Id;
            var foundAs = snap.PathToProfileRoot.TryGetValue(project.RootPath, out var key) ? CompositeKey(key.Item1, key.Item2) : "";
            var trackedAs = CompositeKey(project.ProfileName ?? project.Status.ProfileName ?? "", project.Status.RootName ?? "");
            if (string.Equals(foundAs, trackedAs, StringComparison.OrdinalIgnoreCase))
            {
                _unbound.Remove(id);
                continue;
            }
            if (!_unbound.TryGetValue(id, out var before) || !string.Equals(before, foundAs, StringComparison.OrdinalIgnoreCase))
            {
                _unbound[id] = foundAs;
                _logger.LogInformation("Project {ProjectId}: its root's folder {RootPath} is {Found}. Without a claude it leaves the list once the next read of the roots agrees; with one it carries on until claude exits",
                    id, project.RootPath, foundAs == "" ? "no root now" : $"the root {foundAs} now");
                continue;
            }
            if (!await TryForgetAsync(project)) continue;

            _unbound.Remove(id);
            gone.Add(id);
            if (foundAs == "")
                _logger.LogInformation("Project {ProjectId} left the list: its root at {RootPath} is gone. Its files stay, and it is back if the root is", id, project.RootPath);
            else
            {
                _logger.LogInformation("Project {ProjectId} is no longer tracked under that ID: its root at {RootPath} is {Root} now, and it is recovered under that", id, project.RootPath, foundAs);
                again.Add(project.RootPath);
            }
        }
        foreach (var id in _unbound.Keys.Where(id => !_projects.ContainsKey(id)).ToArray()) _unbound.Remove(id);

        var roots = RootsOf(snap);
        foreach (var path in _recoveredRoots.Keys.Where(path => !roots.Any(root => PathComparer.Equals(root.Path, path))).ToArray())
            _recoveredRoots.Remove(path);
        var toRecover = roots.Where(root => again.Contains(root.Path)
            || !_recoveredRoots.TryGetValue(root.Path, out var was) || !TupleComparer.Instance.Equals(was, (root.Profile, root.Root))).ToArray();
        var recovered = toRecover.Length == 0 ? [] : await RecoverRootsAsync(snap, toRecover);
        foreach (var root in toRecover) _recoveredRoots[root.Path] = (root.Profile, root.Root);

        foreach (var id in gone)
            await PushAsync(() => _hubContext.Clients.All.ProjectDeleted(id), $"project {id} leaving the list");
        foreach (var project in recovered)
        {
            var status = project.Status;
            await PushAsync(() => _hubContext.Clients.All.ProjectCreated(status), $"project {status.Id} joining the list");
        }
        return gone.Count > 0 || recovered.Count > 0;
    }

    /// <summary>
    /// Stops tracking a session that has no claude, running or launching, leaving its files as they
    /// are; false when it has one, or is not tracked. Under its resume lock, so no launch starts meanwhile.
    /// </summary>
    private async Task<bool> TryForgetAsync(ProjectInfo project)
    {
        var id = project.Status.Id;
        var forgotten = await WithResumeLockAsync(project, async () =>
        {
            await _lifecycle.SettleAsync(project);
            return !project.Process.Launching && !_lifecycle.IsRunning(project)
                && _projects.TryRemove(new KeyValuePair<string, ProjectInfo>(id, project));
        });
        if (!forgotten) return false;
        await project.Process.CloseAsync();
        await _pullRequests.ForgetAsync(id);
        return true;
    }

    /// <summary>Starts the poll and the config reload's refresh, once: after the startup's recovery, so every root that appears is one to recover.</summary>
    private void StartWatchingRoots()
    {
        if (Interlocked.Exchange(ref _watching, 1) == 1 || _watchStop.IsCancellationRequested) return;
        _configReload = ChangeToken.OnChange(_configuration.GetReloadToken, () => _ = RefreshInBackgroundAsync("the config was reloaded"));
        if (_rootsPoll > TimeSpan.Zero) _ = PollRootsAsync(_watchStop.Token);
        _logger.LogInformation("Roots are read again on a config reload{Poll}",
            _rootsPoll > TimeSpan.Zero ? $" and every {_rootsPoll.TotalSeconds:0.##}s ({RootsPollSetting})" : $"; the poll is off ({RootsPollSetting} is 0)");
        // Stopped while this started: what it started is stopped too
        if (_watchStop.IsCancellationRequested) _configReload.Dispose();
    }

    private void StopWatchingRoots()
    {
        _watchStop.Cancel();
        _configReload?.Dispose();
    }

    private async Task PollRootsAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(_rootsPoll);
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
                await RefreshInBackgroundAsync("the poll");
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>A refresh nobody waits on: a failure is logged, and the next one tries again.</summary>
    private async Task RefreshInBackgroundAsync(string trigger)
    {
        if (_watchStop.IsCancellationRequested) return;
        try
        {
            await RefreshRootsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the roots again ({Trigger})", trigger);
        }
    }

    /// <summary>
    /// Recovers the sessions of <paramref name="roots"/> from their files, leaving those tracked
    /// already: at the start every root's, then a root's that has appeared, or whose sessions have
    /// another ID now. Returns those recovered.
    /// </summary>
    private async Task<IReadOnlyList<ProjectInfo>> RecoverRootsAsync(ProfileSnapshot recoverSnap, IReadOnlyList<(string Profile, string Root, string Path)> roots)
    {
        // A session tracked already, under the ID it has, is left as it is: one whose claude runs on
        // after its root moved profile, say
        var tracked = new HashSet<string>(_projects.Values.Select(project => FullPath(project.StatePath)), PathComparer);

        // Each root's own working folders, so the root is known exactly: "root" is not a prefix match
        // for "root2". A folder two roots share (two profiles naming one path) is recovered once. The
        // sessions are the state folders in .godmode/sessions/; a flat .godmode/status.json is none
        var seen = new HashSet<string>(PathComparer);
        var sessions = roots
            .SelectMany(root => recoverSnap.ProjectFiles.ListSessions(CompositeKey(root.Profile, root.Root))
                .Select(session => (session.WorkingFolder, session.SessionId, root.Profile, root.Root, RootPath: root.Path)))
            .Where(session => FullPath(ProjectFiles.SessionState.PathOf(session.WorkingFolder, session.SessionId)) is var statePath
                && seen.Add(statePath) && !tracked.Contains(statePath))
            .ToList();

        // An id is unique within its root: one found in two working folders (a folder copied) is the
        // first folder's, in ordinal order, and the other is left on disk, untracked
        var unique = sessions
            .GroupBy(session => ProjectId(session.Profile, session.Root, session.SessionId))
            .Select(same =>
            {
                var ordered = same.OrderBy(session => session.WorkingFolder, StringComparer.Ordinal).ToArray();
                foreach (var other in ordered.Skip(1))
                    _logger.LogWarning("Session {ProjectId} is in {Folder} and in {Other}: the second is not recovered, since an id is unique within its root",
                        same.Key, ordered[0].WorkingFolder, other.WorkingFolder);
                return ordered[0];
            })
            .ToList();

        // Process all sessions in parallel for faster startup
        var recovered = new ConcurrentBag<ProjectInfo>();
        await Parallel.ForEachAsync(unique, async (found, ct) =>
        {
            var (projectPath, sessionId, profileName, rootName, rootPath) = found;
            var statePath = ProjectFiles.SessionState.PathOf(projectPath, sessionId);
            try
            {
                var statusPath = Path.Combine(statePath, ProjectFiles.SessionState.StatusFileName);
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
                // status.json is the project folder's, which its session can write: a pull request
                // link is kept only if it is one the status script's output could have set
                if (status.PullRequest is { } pr && !PullRequestScript.IsValidUrl(pr.Url))
                {
                    _logger.LogWarning("Session at {Path} had a pull request URL that is not http(s); it is dropped", statePath);
                    status = status with { PullRequest = null };
                }

                // Load action name from settings. One that cannot be read may be a shared session's:
                // it is taken as shared, so its delete removes only its state, never the folder
                var settingsRead = ProjectFiles.ProjectSettings.TryLoad(statePath, out var settings);
                if (!settingsRead)
                    _logger.LogWarning("Session at {Path} has no settings.json that can be read: it is taken as sharing its folder, so a delete leaves the folder", statePath);

                // The ID is where the state folder is: its root, and its id. A status.json that says
                // otherwise (its root moved profile) is rewritten below. Nothing else in .godmode holds
                // the ID. Its kind is a label, kept as the id has kinds: lowercase [a-z0-9-]
                var id = ProjectId(profileName, rootName, sessionId);
                var kind = ProjectFiles.SessionState.Kind(status.Kind ?? settings.ActionName);
                var idChanged = status.Id != id || status.Kind != kind;
                if (status.Id != id)
                    _logger.LogInformation("Session at {Path} had ID {OldId}; it is now {ProjectId}", statePath, status.Id, id);

                // The offset is output.jsonl's, not status.json's, which is saved less often than output is written
                var outputOffset = OutputLog.End(statePath);
                var correctedStatus = stateChanged
                    ? status with { Id = id, Kind = kind, State = ProjectState.Stopped, UpdatedAt = DateTime.UtcNow, RootName = rootName, ProfileName = profileName, OutputOffset = outputOffset }
                    : status with { Id = id, Kind = kind, RootName = rootName, ProfileName = profileName, OutputOffset = outputOffset };

                var project = new ProjectInfo
                {
                    Status = correctedStatus,
                    ProjectPath = projectPath,
                    RootPath = rootPath,
                    SessionId = sessionId,
                    ProfileName = profileName,
                    ActionName = settings.ActionName,
                    SharedFolder = !settingsRead || settings.SharedFolder,
                };

                project.ClaudeSessionId = await SessionIdFile.ReadAsync(statePath, _logger, ct);

                // An ID another session has (its old root's, running on under it) is that session's
                if (!_projects.TryAdd(project.Status.Id, project))
                {
                    _logger.LogWarning("Session at {Path} is not recovered: session {ProjectId} is tracked already, in another folder", statePath, project.Status.Id);
                    return;
                }

                // Only save if state or ID changed
                if (stateChanged || idChanged)
                {
                    await _statusUpdater.SaveStatusAsync(project);
                }
                ResumeChecks(project);
                recovered.Add(project);

                _logger.LogInformation("Recovered project {ProjectId} ({Name})", project.Status.Id, project.Status.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recover project from {Path}", statePath);
            }
        });
        return recovered.ToArray();
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
            return _rootConfigReader.ReadConfig(project.RootPath).ResolveAction(project.ActionName) ?? new CreateAction("Create");
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
    /// Refused, and nothing deleted, unless the path is a project folder of the project's root, the
    /// root it was created or recovered in (<see cref="WhyNotAProjectFolderOf"/>): a project whose
    /// folder is elsewhere is forgotten, its folder left be.
    /// </summary>
    private async Task DeleteDirectoryRobustAsync(string path, string rootPath)
    {
        if (!Directory.Exists(path)) return;
        if (WhyNotAProjectFolderOf(rootPath, path) is not null)
            throw new InvalidOperationException($"The project folder '{path}' is not inside a project root, so it is not deleted.");

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
    /// Whether a session other than <paramref name="project"/> has its working folder: tracked, or
    /// only its state on disk. Its delete then leaves the folder, whatever it was created as.
    /// </summary>
    private bool OthersInFolder(ProjectInfo project)
    {
        var path = FullPath(project.ProjectPath);
        return _projects.Values.Any(p => p != project && PathComparer.Equals(FullPath(p.ProjectPath), path))
            || ProjectFiles.SessionState.List(path).Any(id => id != project.SessionId);
    }

    /// <summary>
    /// Removes a session's state, <c>.godmode/sessions/{id}/</c>, and nothing else of its working
    /// folder: the delete of a session that shares its folder. The one place that does, so undo can
    /// move it to <c>.godmode/trash/</c> here instead (#325).
    /// </summary>
    private Task RemoveSessionStateAsync(ProjectInfo project) => DeleteDirectoryRobustAsync(project.StatePath, project.RootPath);

    /// <summary>
    /// Makes the session's state folder, <c>.godmode/sessions/{id}/</c>, with the working folder's
    /// <c>.godmode/.gitignore</c>, and starts its output's generation. Called after scripts run, which
    /// may have created the project dir without .godmode, or checked out one that has a .godmode
    /// without its .gitignore.
    /// </summary>
    private static void CreateSessionState(ProjectInfo project)
    {
        ProjectFiles.SessionState.Create(project.ProjectPath, project.SessionId);

        // A new one each time: an ID created again starts a new generation, so no client resumes into it from an old offset
        OutputLog.StartGeneration(project.StatePath);
    }

    /// <summary>
    /// A session id no session of the root has: <paramref name="first"/> when it is free, else
    /// <paramref name="idWith"/> a new random suffix, tried a few times. Taken means a tracked
    /// session's, one a create in progress claimed, or a state folder on disk in one of the root's
    /// working folders. Throws <see cref="InvalidOperationException"/> when none is free.
    /// </summary>
    private string FreeSessionId(ProfileSnapshot snap, string compositeKey, string profileName, string rootName,
        Func<string, string> idWith, string? first = null)
    {
        const int attempts = 8;
        var id = first ?? idWith(ProjectFiles.SessionState.NewSuffix());
        for (var attempt = 1; ; attempt++)
        {
            var projectId = ProjectId(profileName, rootName, id);
            if (!_projects.ContainsKey(projectId) && !_creatingIds.ContainsKey(projectId) && !snap.ProjectFiles.HasSession(compositeKey, id))
                return id;
            if (attempt == attempts)
                throw new InvalidOperationException($"No free session id in root '{rootName}' after {attempts} tries (last {id}).");
            id = idWith(ProjectFiles.SessionState.NewSuffix());
        }
    }

    /// <summary>
    /// Returns a log file path at the root level for script output.
    /// Uses {rootPath}/logs/{id}.log, the session's id: it survives create script's project dir
    /// delete, and sessions that share a working folder have one each.
    /// </summary>
    private static string GetScriptLogPath(string rootPath, string sessionId) => ScriptFilePath(rootPath, $"{sessionId}.log");

    /// <summary>
    /// Returns a result file path for script-to-server communication, {rootPath}/logs/{id}.result.
    /// Scripts can write key=value pairs (e.g. project_path, project_name) to override defaults.
    /// </summary>
    private static string GetResultFilePath(string rootPath, string sessionId) => ScriptFilePath(rootPath, $"{sessionId}.result");

    private static string ScriptFilePath(string rootPath, string fileName)
    {
        var logsDir = Path.Combine(rootPath, ProjectFiles.ProjectFolder.ScriptLogsFolderName);
        Directory.CreateDirectory(logsDir);
        return Path.Combine(logsDir, fileName);
    }

    /// <summary>Moves a script log or result file to <paramref name="to"/>, replacing one there; nothing when there is none.</summary>
    private static void MoveScriptFile(string from, string to)
    {
        if (File.Exists(from)) File.Move(from, to, overwrite: true);
    }

    /// <summary>
    /// The full path and folder name of a create script's <c>project_path</c>. Refused unless it is a
    /// project folder of its own root (<see cref="WhyNotAProjectFolderOf"/>), and its folder name is a
    /// folder of its own (<c>x/..</c> is not): a delete of the project would delete that recursively.
    /// </summary>
    private static (string Path, string Folder) ValidateScriptProjectPath(string projectPath, string rootPath)
    {
        var fullPath = FullPath(projectPath);
        if (WhyNotAProjectFolderOf(rootPath, fullPath) is { } reason)
            throw new ArgumentException($"The create script's project_path '{projectPath}' {reason}.");
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
        var settings = ProjectFiles.ProjectSettings.Load(project.StatePath);
        // Recovery reads the action name from settings too; a project created before it was saved has none
        project.ActionName ??= settings.ActionName;
        var profileName = project.ProfileName ?? project.Status.ProfileName;
        snap.Profiles.TryGetValue(profileName ?? "", out var profile);

        var (action, stripEnvVarProfile, rootAllowsSkip) = ResolveLaunchAction(project, profileName);
        var (skipPermissions, permissionMode) = LaunchPermissions(project, settings, action, rootAllowsSkip);
        var (env, args) = BuildClaudeConfig(project.ProjectPath, project.StatePath, action, skipPermissions, permissionMode, McpConfigJson(project, IssueProjectToken(project)),
            project.Status.Model ?? action.Model, profile?.Environment, profileName, stripEnvVarProfile);
        return new ClaudeLaunchSpec(env ?? new Dictionary<string, string>(), args);
    }

    /// <summary>
    /// How a launch is permitted, for create, resume, a reply's resume and a restart's alike. Skipping
    /// permissions needs both the project's settings to ask for it and its root's config, as it is now,
    /// to allow it: settings.json is in the project folder, which the session can write, and a planted
    /// or copied folder is relaunched from it unattended. The action name is in that file too, so a launch
    /// needs every action of the root to allow skipping (<paramref name="rootAllowsSkip"/>): a session that
    /// names another action gains nothing. The permission mode is the one the project was
    /// created with (its root's current one, for a project created without), and is checked again for
    /// the same reason; skipping overrides it. Whatever is ignored is logged once per project.
    /// </summary>
    private (bool SkipPermissions, string? PermissionMode) LaunchPermissions(
        ProjectInfo project, ProjectFiles.ProjectSettings settings, CreateAction action, bool rootAllowsSkip)
    {
        var skip = settings.DangerouslySkipPermissions;
        if (skip && !rootAllowsSkip)
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

    /// <summary>The launch warnings said so far, by session state folder and what they are about: each is said once.</summary>
    private readonly ConcurrentDictionary<string, byte> _launchWarnings = new(PathComparer);

    private bool FirstLaunchWarning(ProjectInfo project, string about) =>
        _launchWarnings.TryAdd($"{FullPath(project.StatePath)}\n{about}", 0);

    /// <summary>
    /// The project's action in its root's config (the default action for a project with no root), and
    /// whether every action of the root allows skip-permissions. The root is the project's by its
    /// folder (<see cref="ProjectInfo.RootPath"/>), not by the name it has now.
    /// Throws <see cref="LaunchConfigException"/> when the config cannot be read or lacks the action.
    /// </summary>
    private (CreateAction Action, bool StripEnvVarProfile, bool RootAllowsSkip) ResolveLaunchAction(ProjectInfo project, string? profileName)
    {
        if (project.Status.RootName == null || profileName == null) return (new CreateAction("Create"), false, false);

        RootConfig config;
        try
        {
            config = _rootConfigReader.ReadConfigStrict(project.RootPath);
        }
        catch (Exception ex)
        {
            throw new LaunchConfigException($"root config unreadable: {ex.Message}", ex);
        }
        return config.ResolveAction(project.ActionName) is { } action
            ? (action, config.StripEnvVarProfile, config.GetEffectiveActions().All(a => a.AllowSkipPermissions))
            : throw new LaunchConfigException($"root config has no action '{project.ActionName}'");
    }

    /// <summary>
    /// Builds claude environment and args from action config + the launch's permissions + profile env.
    /// Nothing is pre-approved: a tool call that needs approval reaches the permission prompt, unless
    /// Claude Code's own settings, the permission mode, or skip-permissions, allow it.
    /// </summary>
    private static (Dictionary<string, string>? Env, string[] Args) BuildClaudeConfig(
        string projectPath, string statePath, CreateAction action, bool skipPermissions, string? permissionMode, string mcpConfigJson,
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
        // --mcp-config expects a file path, not inline JSON; the process manager deletes it on exit.
        // It holds the project token, so every launch first makes sure git ignores it
        ProjectFiles.ProjectFolder.EnsureGitIgnore(projectPath);
        args.Add("--mcp-config");
        args.Add(McpConfigFile.Write(statePath, mcpConfigJson));

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
        // Scripts name branches and folders after it. It is not the session's ID, {profile}/{root}/{id}
        env["GODMODE_PROJECT_FOLDER"] = Path.GetFileName(project.ProjectPath);
        env["GODMODE_PROJECT_NAME"] = project.Status.Name;
        // The session's id, yymmdd-{kind}-{slug}-{suffix}: its state is in .godmode/sessions/{id}/. During a create, the id
        // as the action makes it; a kind or project_name in the result file gives the session its final one
        env["GODMODE_SESSION_ID"] = project.SessionId;
        // Whether the session shares its working folder with others (its action's sharedFolder): a
        // script then keys what it makes by the session's id, not the folder, and removes no folder
        env[SharedFolderVariable] = project.SharedFolder ? "true" : "false";

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
