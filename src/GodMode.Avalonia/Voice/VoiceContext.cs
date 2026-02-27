using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using GodMode.Voice.AI;
using GodMode.Voice.Services;

namespace GodMode.Avalonia.Voice;

// --- Supporting Types ---

public sealed record IndexedProject(
    string ProfileName,
    string HostId,
    string HostName,
    ProjectSummary Summary);

public sealed record FocusedProject(
    string ProfileName,
    string HostId,
    string ProjectId,
    string ProjectName);

public sealed class DisambiguationState
{
    public required IReadOnlyList<IndexedProject> Options { get; init; }
    public required string OriginalToolName { get; init; }
    public required IDictionary<string, object> OriginalArgs { get; init; }
    public required string ProjectParamName { get; init; }
}

public sealed class ProjectResolveResult
{
    public bool Resolved { get; init; }
    public IndexedProject? Match { get; init; }
    public IReadOnlyList<IndexedProject>? Candidates { get; init; }
    public string? Error { get; init; }

    public static ProjectResolveResult Ok(IndexedProject match) => new() { Resolved = true, Match = match };
    public static ProjectResolveResult Disambiguate(IReadOnlyList<IndexedProject> candidates) => new() { Candidates = candidates };
    public static ProjectResolveResult Fail(string error) => new() { Error = error };
}

public sealed class ProjectOutputEventArgs : EventArgs
{
    public required ClaudeMessage Message { get; init; }
    public required string ProjectName { get; init; }
}

// --- VoiceContext ---

public sealed class VoiceContext : IDisposable
{
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OldProjectThreshold = TimeSpan.FromDays(30);

    private readonly IProfileService _profileService;
    private readonly IHostConnectionService _hostService;
    private readonly IProjectService _projectService;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private IDisposable? _outputSubscription;

    public string? ActiveProfileName { get; private set; }
    public string? ActiveHostId { get; private set; }
    public string? ActiveHostName { get; private set; }
    public FocusedProject? Focus { get; private set; }
    public IReadOnlyList<IndexedProject> ProjectIndex { get; private set; } = [];
    public DateTime IndexBuiltAt { get; private set; }
    public DisambiguationState? PendingDisambiguation { get; private set; }

    public event EventHandler? ContextChanged;
    public event EventHandler<ProjectOutputEventArgs>? ProjectOutputReceived;

    public VoiceContext(
        IProfileService profileService,
        IHostConnectionService hostService,
        IProjectService projectService)
    {
        _profileService = profileService;
        _hostService = hostService;
        _projectService = projectService;
    }

    public async Task SetProfileAsync(string? profileName)
    {
        await _lock.WaitAsync();
        try
        {
            ActiveProfileName = profileName;
            ActiveHostId = null;
            ActiveHostName = null;
            UnfocusInternal();
            ClearPendingDisambiguation();
            await RefreshProjectIndexInternalAsync();
        }
        finally { _lock.Release(); }
        ContextChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetHostAsync(string? hostId, string? hostName = null)
    {
        await _lock.WaitAsync();
        try
        {
            ActiveHostId = hostId;
            ActiveHostName = hostName;
            UnfocusInternal();
            ClearPendingDisambiguation();
            await RefreshProjectIndexInternalAsync();
        }
        finally { _lock.Release(); }
        ContextChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshProjectIndexAsync()
    {
        await _lock.WaitAsync();
        try { await RefreshProjectIndexInternalAsync(); }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Ensures the index is fresh (auto-refreshes if stale).
    /// </summary>
    public async Task EnsureIndexFreshAsync()
    {
        if (DateTime.UtcNow - IndexBuiltAt > StaleThreshold)
            await RefreshProjectIndexAsync();
    }

    private async Task RefreshProjectIndexInternalAsync()
    {
        var index = new List<IndexedProject>();

        if (ActiveProfileName is not null)
        {
            await IndexProfileAsync(ActiveProfileName, index);
        }
        else
        {
            var profiles = await _profileService.GetProfilesAsync();
            foreach (var profile in profiles)
                await IndexProfileAsync(profile.Name, index);
        }

        ProjectIndex = index.AsReadOnly();
        IndexBuiltAt = DateTime.UtcNow;
    }

    private async Task IndexProfileAsync(string profileName, List<IndexedProject> index)
    {
        IEnumerable<HostInfo> hosts;
        try { hosts = await _hostService.ListAllHostsAsync(profileName); }
        catch { return; }

        foreach (var host in hosts)
        {
            if (ActiveHostId is not null && host.Id != ActiveHostId)
                continue;

            if (!_hostService.IsConnected(profileName, host.Id))
                continue;

            try
            {
                var projects = await _projectService.ListProjectsAsync(profileName, host.Id);
                foreach (var p in projects)
                    index.Add(new IndexedProject(profileName, host.Id, host.Name, p));
            }
            catch
            {
                // Host unreachable — skip
            }
        }
    }

    // --- Smart Project Resolution ---

    public async Task<ProjectResolveResult> ResolveProjectAsync(string projectName)
    {
        await EnsureIndexFreshAsync();
        return ResolveProject(projectName);
    }

    public ProjectResolveResult ResolveProject(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            return ProjectResolveResult.Fail("No project name provided.");

        // 1. Exact match (case-insensitive)
        var exact = ProjectIndex
            .Where(p => p.Summary.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exact.Count == 1)
            return ProjectResolveResult.Ok(exact[0]);

        if (exact.Count > 1)
            return ProjectResolveResult.Disambiguate(exact);

        // 2. Partial contains — filter stale, rank by recency
        var cutoff = DateTime.UtcNow - OldProjectThreshold;
        var partial = ProjectIndex
            .Where(p => p.Summary.Name.Contains(projectName, StringComparison.OrdinalIgnoreCase))
            .Where(p => p.Summary.UpdatedAt > cutoff)
            .OrderByDescending(p => p.Summary.UpdatedAt)
            .ToList();

        if (partial.Count == 1)
            return ProjectResolveResult.Ok(partial[0]);

        if (partial.Count > 1)
            return ProjectResolveResult.Disambiguate(partial);

        // 3. No match — show recent projects
        var recent = ProjectIndex
            .OrderByDescending(p => p.Summary.UpdatedAt)
            .Take(5)
            .ToList();

        var recentNames = recent.Count > 0
            ? string.Join(", ", recent.Select(p => p.Summary.Name))
            : "none";

        return ProjectResolveResult.Fail(
            $"Project '{projectName}' not found. Recent projects: {recentNames}");
    }

    // --- Focus Mode ---

    public async Task FocusProjectAsync(string profileName, string hostId, string projectId, string projectName)
    {
        await _lock.WaitAsync();
        try
        {
            UnfocusInternal();
            Focus = new FocusedProject(profileName, hostId, projectId, projectName);

            try
            {
                var observable = await _projectService.SubscribeOutputAsync(profileName, hostId, projectId);
                _outputSubscription = observable.Subscribe(
                    onNext: message => OnOutputMessage(message, projectName),
                    onError: _ => { });
            }
            catch
            {
                // Subscription failed — focus still set for send_input routing
            }
        }
        finally { _lock.Release(); }
        ContextChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UnfocusProject()
    {
        _lock.Wait();
        try { UnfocusInternal(); }
        finally { _lock.Release(); }
        ContextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UnfocusInternal()
    {
        _outputSubscription?.Dispose();
        _outputSubscription = null;
        Focus = null;
    }

    private void OnOutputMessage(ClaudeMessage message, string projectName)
    {
        // Only surface meaningful messages: assistant text, results, errors
        if (message.Type is not ("assistant" or "result" or "error"))
            return;

        ProjectOutputReceived?.Invoke(this, new ProjectOutputEventArgs
        {
            Message = message,
            ProjectName = projectName
        });
    }

    // --- Disambiguation ---

    public void SetPendingDisambiguation(DisambiguationState state) => PendingDisambiguation = state;
    public void ClearPendingDisambiguation() => PendingDisambiguation = null;

    // --- Effective Resolution (used by tools for host/profile) ---

    public async Task<(string ProfileName, bool Found)> ResolveEffectiveProfileAsync()
    {
        if (ActiveProfileName is not null)
        {
            var profile = await _profileService.GetProfileAsync(ActiveProfileName);
            return profile is not null ? (profile.Name, true) : (ActiveProfileName, false);
        }

        var selected = await _profileService.GetSelectedProfileAsync();
        if (selected is not null) return (selected.Name, true);

        var all = await _profileService.GetProfilesAsync();
        return all.Count > 0 ? (all[0].Name, true) : ("", false);
    }

    public async Task<(HostInfo? Host, string? Error)> ResolveEffectiveHostAsync()
    {
        var (profileName, found) = await ResolveEffectiveProfileAsync();
        if (!found) return (null, "No profile available.");

        if (ActiveHostId is not null)
        {
            var hosts = (await _hostService.ListAllHostsAsync(profileName)).ToList();
            var match = hosts.FirstOrDefault(h => h.Id == ActiveHostId);
            return match is not null ? (match, null) : (null, $"Active server not found.");
        }

        // Auto-select first connected host
        var allHosts = (await _hostService.ListAllHostsAsync(profileName)).ToList();
        var connected = allHosts.FirstOrDefault(h => _hostService.IsConnected(profileName, h.Id));
        if (connected is not null) return (connected, null);

        return allHosts.Count > 0
            ? (allHosts[0], null)
            : (null, $"No servers found for profile '{profileName}'.");
    }

    // --- Context Summary for System Prompt ---

    public VoiceContextSummary GetSummary() => new(
        ActiveProfile: ActiveProfileName,
        ActiveServer: ActiveHostName,
        FocusedProject: Focus?.ProjectName,
        ProjectCount: ProjectIndex.Count);

    public void Dispose()
    {
        _outputSubscription?.Dispose();
        _lock.Dispose();
    }
}
