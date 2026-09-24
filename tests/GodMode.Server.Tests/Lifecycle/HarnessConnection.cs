using System.Security.Claims;
using GodMode.Server.Hubs;
using GodMode.Server.Services;
using GodMode.Shared.Hubs;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// One client connection, calling the real <see cref="ProjectHub"/> as SignalR would: a hub per
/// call, with this connection as its caller. What it receives is in <see cref="Received"/>.
/// </summary>
internal sealed class HarnessConnection
{
    private readonly RecordingHubContext _hub;
    private readonly IProjectManager _projects;
    private readonly ILogger<ProjectHub> _logger;

    public HarnessConnection(string connectionId, RecordingHubContext hub, IProjectManager projects, ILogger<ProjectHub> logger)
    {
        ConnectionId = connectionId;
        _hub = hub;
        _projects = projects;
        _logger = logger;
        hub.Connect(connectionId);
    }

    public string ConnectionId { get; }

    /// <summary>Everything the server pushed to this connection, in the order it was delivered.</summary>
    public IReadOnlyList<HubPush> Received => _hub.Received(ConnectionId);

    public Task SubscribeAsync(string projectId, long outputOffset) => Hub().SubscribeProject(projectId, outputOffset);

    public Task UnsubscribeAsync(string projectId) => Hub().UnsubscribeProject(projectId);

    /// <summary>The connection drops: the hub hears it, and SignalR takes it out of its groups.</summary>
    public async Task DisconnectAsync()
    {
        _hub.Disconnect(ConnectionId);
        await Hub().OnDisconnectedAsync(null);
    }

    private ProjectHub Hub() => new(_projects, _logger)
    {
        Context = new CallerContext(ConnectionId),
        Groups = _hub.Groups,
        Clients = new CallerClients(_hub.Clients, ConnectionId),
    };

    private sealed class CallerContext(string connectionId) : HubCallerContext
    {
        public override string ConnectionId => connectionId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    private sealed class CallerClients(IHubClients<IProjectHubClient> clients, string connectionId) : IHubCallerClients<IProjectHubClient>
    {
        public IProjectHubClient Caller => clients.Client(connectionId);
        public IProjectHubClient Others => clients.AllExcept([connectionId]);
        public IProjectHubClient OthersInGroup(string groupName) => clients.GroupExcept(groupName, [connectionId]);
        public IProjectHubClient All => clients.All;
        public IProjectHubClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => clients.AllExcept(excludedConnectionIds);
        public IProjectHubClient Client(string id) => clients.Client(id);
        public IProjectHubClient Clients(IReadOnlyList<string> connectionIds) => clients.Clients(connectionIds);
        public IProjectHubClient Group(string groupName) => clients.Group(groupName);
        public IProjectHubClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => clients.GroupExcept(groupName, excludedConnectionIds);
        public IProjectHubClient Groups(IReadOnlyList<string> groupNames) => clients.Groups(groupNames);
        public IProjectHubClient User(string userId) => clients.User(userId);
        public IProjectHubClient Users(IReadOnlyList<string> userIds) => clients.Users(userIds);
    }
}
