using System.Collections.Concurrent;
using GodMode.Server.Hubs;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>One call the server made to its hub clients, whoever it was addressed to.</summary>
/// <param name="Offset">An output line's offset (OutputReceived), a batch's fromOffset (OutputBatch), or where a replay completed.</param>
/// <param name="Attention">The list an AttentionChanged pushed.</param>
internal sealed record HubPush(string Method, string? ProjectId, ProjectStatus? Status = null, string? RawJson = null,
    long? Offset = null, IReadOnlyList<OutputLine>? Lines = null, IReadOnlyList<AttentionItem>? Attention = null);

/// <summary>
/// Stands in for the server's hub context: every push to any client is recorded, in order, as a
/// connected client would receive it. <see cref="HoldOutput"/> makes output broadcasts wait, which
/// holds the project's consumer on the line it is broadcasting while later lines queue behind it.
/// Connections made with <see cref="Connect"/> also keep their own inbox: what SignalR would deliver
/// to that connection, by its address and the groups it is in when the push is made.
/// </summary>
internal sealed class RecordingHubContext : IHubContext<ProjectHub, IProjectHubClient>
{
    private readonly ConcurrentQueue<HubPush> _pushes = new();
    // Open connections; what each got is kept in _received, so a test can read it after a disconnect
    private readonly ConcurrentDictionary<string, byte> _open = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<HubPush>> _received = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _groups = new();
    private volatile TaskCompletionSource? _outputGate;

    public RecordingHubContext()
    {
        Clients = new Addresses(this);
        Groups = new GroupManager(this);
    }

    public IHubClients<IProjectHubClient> Clients { get; }
    public IGroupManager Groups { get; }

    public IReadOnlyList<HubPush> Pushes => _pushes.ToArray();

    /// <summary>The <c>StatusChanged</c> pushes for one project, oldest first.</summary>
    public IReadOnlyList<ProjectStatus> StatusPushes(string projectId) =>
        Pushes.Where(p => p.Method == nameof(IProjectHubClient.StatusChanged) && p.ProjectId == projectId)
            .Select(p => p.Status!).ToArray();

    /// <summary>The lists <c>AttentionChanged</c> pushed, oldest first.</summary>
    public IReadOnlyList<IReadOnlyList<AttentionItem>> AttentionPushes =>
        Pushes.Where(p => p.Method == nameof(IProjectHubClient.AttentionChanged)).Select(p => p.Attention!).ToArray();

    /// <summary>Output broadcasts wait from now until the returned gate is released.</summary>
    public TaskCompletionSource HoldOutput() =>
        _outputGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Releases held output broadcasts; later ones go through at once.</summary>
    public void ReleaseOutput()
    {
        var gate = _outputGate;
        _outputGate = null;
        gate?.TrySetResult();
    }

    // ── Connections ──

    /// <summary>A connection is open from here on: it gets pushes to all clients, to it and to its groups.</summary>
    public void Connect(string connectionId) => _open.TryAdd(connectionId, 0);

    /// <summary>The connection closed: as SignalR does, it leaves every group and gets nothing more.</summary>
    public void Disconnect(string connectionId)
    {
        _open.TryRemove(connectionId, out _);
        foreach (var members in _groups.Values) members.TryRemove(connectionId, out _);
    }

    /// <summary>Everything delivered to the connection while it was open, in the order it was delivered.</summary>
    public IReadOnlyList<HubPush> Received(string connectionId) =>
        _received.TryGetValue(connectionId, out var inbox) ? inbox.ToArray() : [];

    private IEnumerable<string> Members(string group) =>
        _groups.TryGetValue(group, out var members) ? members.Keys : [];

    private void Record(HubPush push, IEnumerable<string> recipients)
    {
        _pushes.Enqueue(push);
        foreach (var connectionId in recipients.Distinct())
            if (_open.ContainsKey(connectionId))
                _received.GetOrAdd(connectionId, _ => new ConcurrentQueue<HubPush>()).Enqueue(push);
    }

    /// <summary>The client proxy for one address; it records each push to whoever the address reaches at the time.</summary>
    private sealed class RecordingClient(RecordingHubContext hub, Func<IEnumerable<string>> recipients) : IProjectHubClient
    {
        public async Task OutputReceived(string projectId, long offset, string rawJson)
        {
            if (hub._outputGate is { } gate) await gate.Task;
            await Done(new HubPush(nameof(OutputReceived), projectId, RawJson: rawJson, Offset: offset));
        }

        public Task OutputBatch(string projectId, long fromOffset, IReadOnlyList<OutputLine> lines) =>
            Done(new HubPush(nameof(OutputBatch), projectId, Offset: fromOffset, Lines: lines));

        public Task OutputReplayComplete(string projectId, long offset) =>
            Done(new HubPush(nameof(OutputReplayComplete), projectId, Offset: offset));

        public Task StatusChanged(string projectId, ProjectStatus status) =>
            Done(new HubPush(nameof(StatusChanged), projectId, status));

        public Task AttentionChanged(AttentionItem[] items) =>
            Done(new HubPush(nameof(AttentionChanged), null, Attention: items));

        public Task ProjectCreated(ProjectStatus status) => Done(new HubPush(nameof(ProjectCreated), status.Id, status));
        public Task CreationProgress(string projectId, string message) => Done(new HubPush(nameof(CreationProgress), projectId));
        public Task ProjectDeleted(string projectId) => Done(new HubPush(nameof(ProjectDeleted), projectId));

        private Task Done(HubPush push)
        {
            hub.Record(push, recipients());
            return Task.CompletedTask;
        }
    }

    private sealed class Addresses(RecordingHubContext hub) : IHubClients<IProjectHubClient>
    {
        private IProjectHubClient To(Func<IEnumerable<string>> recipients) => new RecordingClient(hub, recipients);

        public IProjectHubClient All => To(() => hub._open.Keys);
        public IProjectHubClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => To(() => hub._open.Keys.Except(excludedConnectionIds));
        public IProjectHubClient Client(string connectionId) => To(() => [connectionId]);
        public IProjectHubClient Clients(IReadOnlyList<string> connectionIds) => To(() => connectionIds);
        public IProjectHubClient Group(string groupName) => To(() => hub.Members(groupName));
        public IProjectHubClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) =>
            To(() => hub.Members(groupName).Except(excludedConnectionIds));
        public IProjectHubClient Groups(IReadOnlyList<string> groupNames) => To(() => groupNames.SelectMany(hub.Members));
        public IProjectHubClient User(string userId) => To(() => hub._open.Keys);
        public IProjectHubClient Users(IReadOnlyList<string> userIds) => To(() => hub._open.Keys);
    }

    private sealed class GroupManager(RecordingHubContext hub) : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            if (hub._open.ContainsKey(connectionId))
                hub._groups.GetOrAdd(groupName, _ => new ConcurrentDictionary<string, byte>())[connectionId] = 0;
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            if (hub._groups.TryGetValue(groupName, out var members)) members.TryRemove(connectionId, out _);
            return Task.CompletedTask;
        }
    }
}
