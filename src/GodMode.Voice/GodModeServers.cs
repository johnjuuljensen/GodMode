using System.Collections.Concurrent;
using System.Text.Json;
using GodMode.ClientBase.Hub;
using GodMode.ClientBase.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GodMode.Voice;

/// <summary>An attention item and the server it is on.</summary>
public sealed record ServerAttentionItem(string ServerId, string ServerName, AttentionItem Item)
{
    public ProjectRef Project => new(ServerId, Item.ProjectId);
}

/// <summary>A project and the server it is on.</summary>
public sealed record ServerProject(string ServerId, string ServerName, ProjectSummary Project)
{
    public ProjectRef Ref => new(ServerId, Project.Id);
}

/// <summary>A project root and the server it is on.</summary>
public sealed record ServerRoot(string ServerId, string ServerName, ProjectRootInfo Root)
{
    /// <summary>The root's profile, as the app names it: <c>Default</c> when it has none.</summary>
    public string Profile => Root.ProfileName ?? "Default";

    /// <summary>What the root is said as: its title, else its name (#434). Its name stays its key.</summary>
    public string Shown => Root.Title ?? Root.Name;
}

/// <summary>How many of the servers voice connects to had given their lists when the session started.</summary>
public sealed record ServersHeard(int Answered, int Servers)
{
    /// <summary>There are servers, and none of them answered: voice knows no project.</summary>
    public bool NoneAnswered => Servers > 0 && Answered == 0;
}

/// <summary>What voice does on the servers, through their hubs as they are.</summary>
public interface IGodModeServers
{
    /// <summary>
    /// A server's whole attention list: on each connection (what it holds then), and each time it changes
    /// (<see cref="IProjectHubClient.AttentionChanged"/>).
    /// </summary>
    event Action<string, string, IReadOnlyList<AttentionItem>>? AttentionChanged;

    /// <summary>
    /// A server's whole list of projects: on each connection (what it holds then), each time one is created, changes
    /// or is deleted (<see cref="IProjectHubClient.ProjectCreated"/>, <see cref="IProjectHubClient.StatusChanged"/>,
    /// <see cref="IProjectHubClient.ProjectDeleted"/>), and empty when the server is let go of.
    /// </summary>
    event Action<string, string, IReadOnlyList<ProjectSummary>>? ProjectsChanged;

    /// <summary>
    /// A server's whole list of roots, for their titles (#434): on each connection, before its projects, each time
    /// they change (<see cref="IProjectHubClient.RootsChanged"/>), and empty when the server is let go of.
    /// </summary>
    event Action<string, IReadOnlyList<ProjectRootInfo>>? RootsChanged;

    /// <summary>What needs the user on every server connected now (<see cref="IProjectHub.GetAttention"/>), oldest first.
    /// A server that fails to answer is left out.</summary>
    Task<IReadOnlyList<ServerAttentionItem>> GetAttentionAsync(CancellationToken ct);

    Task<ProjectStatus> GetStatusAsync(ProjectRef project, CancellationToken ct);

    /// <summary><see cref="IProjectHub.ReplyByVoice"/>: the answer reaches the session marked as transcribed speech.</summary>
    Task ReplyAsync(ProjectRef project, string text, CancellationToken ct);

    /// <summary>
    /// <see cref="IProjectHub.AnswerQuestion"/>: the project's pending AskUserQuestion answered with the options the user
    /// picked (#529), each question's text to its label, as the inbox answers it.
    /// </summary>
    Task AnswerQuestionAsync(ProjectRef project, string requestId, IReadOnlyDictionary<string, string> answers, CancellationToken ct);

    /// <summary><see cref="IProjectHub.AskForRecap"/>: sends <c>/recap</c> to a project with no recap, once (#513).</summary>
    Task<RecapAsk> AskForRecapAsync(ProjectRef project, CancellationToken ct);

    Task MarkSeenAsync(ProjectRef project, CancellationToken ct);

    /// <summary><see cref="IProjectHub.SetImportance"/>: how much the project may interrupt the user.</summary>
    Task SetImportanceAsync(ProjectRef project, Importance importance, CancellationToken ct);

    /// <summary>
    /// <see cref="IProjectHub.DeleteProject"/> as the app's delete calls it (#532): never forced, so the root's delete
    /// script may refuse, which fails it, saying why.
    /// </summary>
    Task<DeleteProjectResult> DeleteAsync(ProjectRef project, CancellationToken ct);

    /// <summary><see cref="IProjectHub.ForgetProject"/>: the session out of GodMode, its folder kept, no delete script run.</summary>
    Task<DeleteProjectResult> ForgetAsync(ProjectRef project, CancellationToken ct);

    /// <summary>What claude said in the project's last <paramref name="turns"/> turns, oldest first (<see cref="IProjectHub.GetLastReplies"/>).</summary>
    Task<IReadOnlyList<AssistantReply>> GetLastRepliesAsync(ProjectRef project, int turns, CancellationToken ct);

    /// <summary>Every root on every server connected now (<see cref="IProjectHub.ListProjectRoots"/>). A server that fails to answer is left out.</summary>
    Task<IReadOnlyList<ServerRoot>> ListRootsAsync(CancellationToken ct);

    /// <summary><see cref="IProjectHub.CreateProject"/> in the root, as the app's create form calls it: the form's values as strings.</summary>
    Task<CreateProjectResult> CreateAsync(ServerRoot root, string actionName, IReadOnlyDictionary<string, string> inputs, CancellationToken ct);

    /// <summary>The issue's title and labels from the root's issueInfo script (<see cref="IProjectHub.DescribeIssue"/>); null when it has none.</summary>
    Task<IssueInfo?> DescribeIssueAsync(ServerRoot root, string issue, CancellationToken ct);
}

/// <summary>
/// <see cref="IGodModeServers"/> over one hub connection per server of the <see cref="IServerDirectory"/>
/// (<see cref="ServerConnections"/>, as the attention notifications have).
/// </summary>
public sealed class HubServers : IGodModeServers, IServerConnectionHandler, IAsyncDisposable
{
    private readonly ServerConnections _connections;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, string> _names = new();
    private readonly ConcurrentDictionary<string, byte> _listed = new();
    private readonly ConcurrentDictionary<string, ServerProjects> _projects = new();
    private readonly TimeSpan _firstAnswerWait;

    /// <summary>
    /// How long a start waits, in all, for the first server to answer when none has within its wait
    /// (<see cref="ConnectAsync"/>): a local server's connection can take a few seconds to come up.
    /// </summary>
    public static readonly TimeSpan DefaultFirstAnswerWait = TimeSpan.FromSeconds(15);

    public HubServers(IServerDirectory directory, ILoggerFactory loggerFactory, TimeSpan? retryDelay = null, TimeSpan? maxRetryDelay = null,
        TimeSpan? firstAnswerWait = null)
    {
        _logger = loggerFactory.CreateLogger<HubServers>();
        _firstAnswerWait = firstAnswerWait ?? DefaultFirstAnswerWait;
        _connections = new ServerConnections(directory, this, _logger, "Voice", retryDelay, maxRetryDelay);
    }

    public event Action<string, string, IReadOnlyList<AttentionItem>>? AttentionChanged;

    public event Action<string, string, IReadOnlyList<ProjectSummary>>? ProjectsChanged;

    public event Action<string, IReadOnlyList<ProjectRootInfo>>? RootsChanged;

    /// <summary>Connects to the servers listed now, and lets go of those gone (<see cref="ServerConnections.RefreshAsync"/>).</summary>
    public Task<int> RefreshAsync(CancellationToken ct = default) => _connections.RefreshAsync(ct);

    /// <summary>
    /// Connects to the servers listed now, and waits until each has given its attention list, or
    /// <paramref name="wait"/> has passed (a server that is down is not waited for longer). When none has answered
    /// by then, it waits on for the first that does, until the first-answer wait has passed since it began (issue #381:
    /// a session that starts knowing no project answers every project "Ukendt").
    /// </summary>
    public async Task<ServersHeard> ConnectAsync(TimeSpan wait, CancellationToken ct = default)
    {
        var started = TimeProvider.System.GetTimestamp();
        var servers = await RefreshAsync(ct);
        if (!await WaitAsync(() => _listed.Count >= servers, wait, ct))
        {
            _logger.LogInformation("Voice: {Listed} of {Servers} servers answered within {Wait}", _listed.Count, servers, wait);
            var rest = _firstAnswerWait - TimeProvider.System.GetElapsedTime(started);
            if (_listed.IsEmpty && rest > TimeSpan.Zero)
            {
                if (await WaitAsync(() => !_listed.IsEmpty, rest, ct))
                    _logger.LogInformation("Voice: {Listed} of {Servers} servers answered within {Wait}", _listed.Count, servers, _firstAnswerWait);
                else
                    _logger.LogWarning("Voice: no server answered within {Wait}; starting without projects", _firstAnswerWait);
            }
        }
        return new ServersHeard(_listed.Count, servers);
    }

    /// <summary>Whether <paramref name="done"/> came true within <paramref name="wait"/>.</summary>
    private static async Task<bool> WaitAsync(Func<bool> done, TimeSpan wait, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        try
        {
            while (!done())
                await Task.Delay(50, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return done();
        }
    }

    /// <summary>Makes every connection again (the network changed).</summary>
    public void Reconnect() => _connections.Reconnect();

    /// <summary>How many servers are connected now.</summary>
    public int ConnectedCount => _connections.Connected.Count;

    public async Task<IReadOnlyList<ServerAttentionItem>> GetAttentionAsync(CancellationToken ct) =>
        [.. (await EachServerAsync(async (server, hub) =>
                (await hub.InvokeAsync<AttentionItem[]>(nameof(IProjectHub.GetAttention), ct))
                .Select(item => new ServerAttentionItem(server.Id, server.Name, item))))
            .OrderBy(i => i.Item.Since)];

    public Task<ProjectStatus> GetStatusAsync(ProjectRef project, CancellationToken ct) =>
        Hub(project).InvokeAsync<ProjectStatus>(nameof(IProjectHub.GetStatus), project.ProjectId, ct);

    public Task ReplyAsync(ProjectRef project, string text, CancellationToken ct) =>
        Hub(project).InvokeAsync(nameof(IProjectHub.ReplyByVoice), project.ProjectId, text, ct);

    public Task AnswerQuestionAsync(ProjectRef project, string requestId, IReadOnlyDictionary<string, string> answers, CancellationToken ct) =>
        Hub(project).InvokeAsync(nameof(IProjectHub.AnswerQuestion), project.ProjectId, requestId, new Dictionary<string, string>(answers), ct);

    public Task<RecapAsk> AskForRecapAsync(ProjectRef project, CancellationToken ct) =>
        Hub(project).InvokeAsync<RecapAsk>(nameof(IProjectHub.AskForRecap), project.ProjectId, ct);

    public Task MarkSeenAsync(ProjectRef project, CancellationToken ct) =>
        Hub(project).InvokeAsync(nameof(IProjectHub.MarkSeen), project.ProjectId, ct);

    public Task SetImportanceAsync(ProjectRef project, Importance importance, CancellationToken ct) =>
        Hub(project).InvokeAsync(nameof(IProjectHub.SetImportance), project.ProjectId, importance, ct);

    public Task<DeleteProjectResult> DeleteAsync(ProjectRef project, CancellationToken ct) =>
        Hub(project).InvokeAsync<DeleteProjectResult>(nameof(IProjectHub.DeleteProject), project.ProjectId, false, ct);

    public Task<DeleteProjectResult> ForgetAsync(ProjectRef project, CancellationToken ct) =>
        Hub(project).InvokeAsync<DeleteProjectResult>(nameof(IProjectHub.ForgetProject), project.ProjectId, ct);

    public async Task<IReadOnlyList<AssistantReply>> GetLastRepliesAsync(ProjectRef project, int turns, CancellationToken ct) =>
        await Hub(project).InvokeAsync<AssistantReply[]>(nameof(IProjectHub.GetLastReplies), project.ProjectId, turns, ct);

    public async Task<IReadOnlyList<ServerRoot>> ListRootsAsync(CancellationToken ct) =>
        [.. await EachServerAsync(async (server, hub) =>
            (await hub.InvokeAsync<ProjectRootInfo[]>(nameof(IProjectHub.ListProjectRoots), ct))
            .Select(root => new ServerRoot(server.Id, server.Name, root)))];

    public Task<CreateProjectResult> CreateAsync(ServerRoot root, string actionName, IReadOnlyDictionary<string, string> inputs, CancellationToken ct) =>
        Hub(root.ServerId).InvokeAsync<CreateProjectResult>(nameof(IProjectHub.CreateProject), root.Profile, root.Root.Name, actionName,
            inputs.ToDictionary(i => i.Key, i => JsonSerializer.SerializeToElement(i.Value)), ct);

    public Task<IssueInfo?> DescribeIssueAsync(ServerRoot root, string issue, CancellationToken ct) =>
        Hub(root.ServerId).InvokeAsync<IssueInfo?>(nameof(IProjectHub.DescribeIssue), root.Profile, root.Root.Name, issue, ct);

    public ValueTask DisposeAsync() => _connections.DisposeAsync();

    private HubConnection Hub(ProjectRef project) => Hub(project.ServerId);

    private HubConnection Hub(string serverId) =>
        _connections.ConnectionTo(serverId)
        ?? throw new InvalidOperationException($"Not connected to the server {_names.GetValueOrDefault(serverId, serverId)}");

    /// <summary>The call on every connected server at once; one that fails is logged and left out.</summary>
    private async Task<IEnumerable<T>> EachServerAsync<T>(Func<ConnectedServer, HubConnection, Task<IEnumerable<T>>> call)
    {
        var results = await Task.WhenAll(_connections.Connected.Select(async c =>
        {
            try
            {
                return await call(c.Server, c.Connection);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Voice: server {ServerId} did not answer: {Error}", c.Server.Id, ex.Message);
                return [];
            }
        }));
        return results.SelectMany(r => r);
    }

    void IServerConnectionHandler.Configure(ConnectedServer server, HubConnection connection)
    {
        connection.On<AttentionItem[]>(nameof(IProjectHubClient.AttentionChanged),
            items => AttentionChanged?.Invoke(server.Id, server.Name, items));
        connection.On<ProjectStatus>(nameof(IProjectHubClient.ProjectCreated), status => Projects(server).Created(status));
        connection.On<string, ProjectStatus>(nameof(IProjectHubClient.StatusChanged), (id, status) => Projects(server).StatusChanged(id, status));
        connection.On<string>(nameof(IProjectHubClient.ProjectDeleted), id => Projects(server).Deleted(id));
        connection.On<ProjectRootInfo[], ProfileInfo[]>(nameof(IProjectHubClient.RootsChanged), (roots, _) => RootsChanged?.Invoke(server.Id, roots));
    }

    async Task IServerConnectionHandler.OnConnectedAsync(ConnectedServer server, HubConnection connection, CancellationToken ct)
    {
        _names[server.Id] = server.Name;
        // Roots before projects, so a project is said with its root's title from the first
        RootsChanged?.Invoke(server.Id, await connection.InvokeAsync<ProjectRootInfo[]>(nameof(IProjectHub.ListProjectRoots), ct));
        // Projects before attention, so a project's handle is given with its root and kind
        // What the hub pushes while the list is on its way is made after it, on top of it
        var list = Projects(server);
        list.BeginListing();
        ProjectSummary[]? projects = null;
        try
        {
            projects = await connection.InvokeAsync<ProjectSummary[]>(nameof(IProjectHub.ListProjects), ct);
        }
        finally
        {
            list.EndListing(projects);
        }
        var items = await connection.InvokeAsync<AttentionItem[]>(nameof(IProjectHub.GetAttention), ct);
        AttentionChanged?.Invoke(server.Id, server.Name, items);
        _listed[server.Id] = 0;
    }

    void IServerConnectionHandler.OnRemoved(string serverId)
    {
        _listed.TryRemove(serverId, out _);
        RootsChanged?.Invoke(serverId, []);
        if (_projects.TryRemove(serverId, out var projects))
            projects.Removed();
        if (_names.TryRemove(serverId, out var name))
            AttentionChanged?.Invoke(serverId, name, []);
    }

    private ServerProjects Projects(ConnectedServer server) =>
        _projects.GetOrAdd(server.Id, _ => new ServerProjects(server.Id, server.Name, (id, name, list) => ProjectsChanged?.Invoke(id, name, list)));

    /// <summary>
    /// One server's projects as last heard, from its list and the hub's events. A change and the list it pushes are
    /// one step, so the lists are pushed in the order the changes were made. While the server's list is on its way
    /// (<see cref="BeginListing"/>), changes wait, and are made on top of the list when it comes: a project created or
    /// deleted meanwhile is neither wiped by the list nor brought back by it.
    /// </summary>
    internal sealed class ServerProjects(string serverId, string serverName, Action<string, string, IReadOnlyList<ProjectSummary>> push)
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, ProjectSummary> _projects = [];
        private List<Func<Dictionary<string, ProjectSummary>, bool>>? _waiting;
        private bool _removed;

        /// <summary><see cref="IProjectHubClient.ProjectCreated"/>.</summary>
        public void Created(ProjectStatus status) => Change(p =>
        {
            p[status.Id] = Summary(status);
            return true;
        });

        /// <summary>
        /// <see cref="IProjectHubClient.StatusChanged"/>, of a project it knows only: a change that comes after the
        /// project's delete does not bring it back.
        /// </summary>
        public void StatusChanged(string id, ProjectStatus status) => Change(p =>
        {
            if (!p.ContainsKey(id)) return false;
            p[id] = Summary(status);
            return true;
        });

        /// <summary><see cref="IProjectHubClient.ProjectDeleted"/>.</summary>
        public void Deleted(string id) => Change(p => p.Remove(id));

        public void BeginListing()
        {
            lock (_lock) _waiting ??= [];
        }

        /// <summary>The list came (or, null, did not): it replaces what was heard, then the changes that waited are made.</summary>
        public void EndListing(IReadOnlyList<ProjectSummary>? listed)
        {
            lock (_lock)
            {
                var waiting = _waiting ?? [];
                _waiting = null;
                if (_removed) return;
                if (listed is not null)
                {
                    _projects.Clear();
                    foreach (var project in listed) _projects[project.Id] = project;
                }
                foreach (var change in waiting) change(_projects);
                Push();
            }
        }

        /// <summary>The server is let go of: it has no projects, and hears of none any more.</summary>
        public void Removed()
        {
            lock (_lock)
            {
                _removed = true;
                _waiting = null;
                _projects.Clear();
                Push();
            }
        }

        private void Change(Func<Dictionary<string, ProjectSummary>, bool> change)
        {
            lock (_lock)
            {
                if (_removed) return;
                if (_waiting is not null)
                    _waiting.Add(change);
                else if (change(_projects))
                    Push();
            }
        }

        private void Push() => push(serverId, serverName, [.. _projects.Values]);

        private static ProjectSummary Summary(ProjectStatus s) => ProjectSummary.Of(s);
    }

    void IServerConnectionHandler.OnListedCompletely(IReadOnlySet<string> serverIds) { }
}
