using System.Collections.Concurrent;
using GodMode.ClientBase.Hub;
using GodMode.ClientBase.Services;
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

    /// <summary>What needs the user on every server connected now (<see cref="IProjectHub.GetAttention"/>), oldest first.
    /// A server that fails to answer is left out.</summary>
    Task<IReadOnlyList<ServerAttentionItem>> GetAttentionAsync(CancellationToken ct);

    Task<ProjectStatus> GetStatusAsync(ProjectRef project, CancellationToken ct);

    /// <summary><see cref="IProjectHub.ReplyAndResume"/>.</summary>
    Task ReplyAsync(ProjectRef project, string text, CancellationToken ct);

    Task MarkSeenAsync(ProjectRef project, CancellationToken ct);
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

    public HubServers(IServerDirectory directory, ILoggerFactory loggerFactory, TimeSpan? retryDelay = null, TimeSpan? maxRetryDelay = null)
    {
        _logger = loggerFactory.CreateLogger<HubServers>();
        _connections = new ServerConnections(directory, this, _logger, "Voice", retryDelay, maxRetryDelay);
    }

    public event Action<string, string, IReadOnlyList<AttentionItem>>? AttentionChanged;

    public event Action<string, string, IReadOnlyList<ProjectSummary>>? ProjectsChanged;

    /// <summary>Connects to the servers listed now, and lets go of those gone (<see cref="ServerConnections.RefreshAsync"/>).</summary>
    public Task<int> RefreshAsync(CancellationToken ct = default) => _connections.RefreshAsync(ct);

    /// <summary>
    /// Connects to the servers listed now, and waits until each has given its attention list, or
    /// <paramref name="wait"/> has passed (a server that is down is not waited for longer).
    /// </summary>
    public async Task ConnectAsync(TimeSpan wait, CancellationToken ct = default)
    {
        var servers = await RefreshAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        try
        {
            while (_listed.Count < servers)
                await Task.Delay(50, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogInformation("Voice: {Listed} of {Servers} servers answered within {Wait}", _listed.Count, servers, wait);
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
        Hub(project).InvokeAsync(nameof(IProjectHub.ReplyAndResume), project.ProjectId, text, ct);

    public Task MarkSeenAsync(ProjectRef project, CancellationToken ct) =>
        Hub(project).InvokeAsync(nameof(IProjectHub.MarkSeen), project.ProjectId, ct);

    public ValueTask DisposeAsync() => _connections.DisposeAsync();

    private HubConnection Hub(ProjectRef project) =>
        _connections.ConnectionTo(project.ServerId)
        ?? throw new InvalidOperationException($"Not connected to the server {_names.GetValueOrDefault(project.ServerId, project.ServerId)}");

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
        connection.On<ProjectStatus>(nameof(IProjectHubClient.ProjectCreated),
            status => Projects(server).Change(p =>
            {
                p[status.Id] = Summary(status);
                return true;
            }));
        // Only a project it knows: a change that comes after the project's delete does not bring it back
        connection.On<string, ProjectStatus>(nameof(IProjectHubClient.StatusChanged),
            (id, status) => Projects(server).Change(p =>
            {
                if (!p.ContainsKey(id)) return false;
                p[id] = Summary(status);
                return true;
            }));
        connection.On<string>(nameof(IProjectHubClient.ProjectDeleted),
            id => Projects(server).Change(p => p.Remove(id)));
    }

    async Task IServerConnectionHandler.OnConnectedAsync(ConnectedServer server, HubConnection connection, CancellationToken ct)
    {
        _names[server.Id] = server.Name;
        // Projects before attention, so a project's handle is given with its root and kind
        var projects = await connection.InvokeAsync<ProjectSummary[]>(nameof(IProjectHub.ListProjects), ct);
        Projects(server).Change(p =>
        {
            p.Clear();
            foreach (var project in projects) p[project.Id] = project;
            return true;
        });
        var items = await connection.InvokeAsync<AttentionItem[]>(nameof(IProjectHub.GetAttention), ct);
        AttentionChanged?.Invoke(server.Id, server.Name, items);
        _listed[server.Id] = 0;
    }

    void IServerConnectionHandler.OnRemoved(string serverId)
    {
        _listed.TryRemove(serverId, out _);
        if (_projects.TryRemove(serverId, out var projects))
            projects.Change(p =>
            {
                p.Clear();
                return true;
            });
        if (_names.TryRemove(serverId, out var name))
            AttentionChanged?.Invoke(serverId, name, []);
    }

    private ServerProjects Projects(ConnectedServer server) =>
        _projects.GetOrAdd(server.Id, _ => new ServerProjects(server.Id, server.Name, this));

    private static ProjectSummary Summary(ProjectStatus s) =>
        new(s.Id, s.Name, s.State, s.UpdatedAt, s.CurrentQuestion, s.RootName, s.ProfileName, s.PendingPermission,
            s.PendingQuestion, s.PullRequest, s.Kind, s.ActionName, s.SharedFolder);

    /// <summary>
    /// One server's projects as last heard. A change and the list it pushes are one step, so the lists are pushed in
    /// the order the changes were made.
    /// </summary>
    private sealed class ServerProjects(string serverId, string serverName, HubServers owner)
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, ProjectSummary> _projects = [];

        /// <summary>Makes the change, and pushes the whole list when it says it changed something.</summary>
        public void Change(Func<Dictionary<string, ProjectSummary>, bool> change)
        {
            lock (_lock)
            {
                if (change(_projects))
                    owner.ProjectsChanged?.Invoke(serverId, serverName, [.. _projects.Values]);
            }
        }
    }

    void IServerConnectionHandler.OnListedCompletely(IReadOnlySet<string> serverIds) { }
}
