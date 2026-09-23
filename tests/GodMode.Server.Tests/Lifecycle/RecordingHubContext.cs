using System.Collections.Concurrent;
using GodMode.Server.Hubs;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>One call the server made to its hub clients, whoever it was addressed to.</summary>
internal sealed record HubPush(string Method, string? ProjectId, ProjectStatus? Status = null, string? RawJson = null);

/// <summary>
/// Stands in for the server's hub context: every push to any client is recorded, in order, as a
/// connected client would receive it. <see cref="HoldOutput"/> makes output broadcasts wait, which
/// holds the project's consumer on the line it is broadcasting while later lines queue behind it.
/// </summary>
internal sealed class RecordingHubContext : IHubContext<ProjectHub, IProjectHubClient>
{
    private readonly ConcurrentQueue<HubPush> _pushes = new();
    private volatile TaskCompletionSource? _outputGate;

    public RecordingHubContext()
    {
        var client = new RecordingClient(this);
        Clients = new AllClients(client);
    }

    public IHubClients<IProjectHubClient> Clients { get; }
    public IGroupManager Groups { get; } = new NoGroups();

    public IReadOnlyList<HubPush> Pushes => _pushes.ToArray();

    /// <summary>The <c>StatusChanged</c> pushes for one project, oldest first.</summary>
    public IReadOnlyList<ProjectStatus> StatusPushes(string projectId) =>
        Pushes.Where(p => p.Method == nameof(IProjectHubClient.StatusChanged) && p.ProjectId == projectId)
            .Select(p => p.Status!).ToArray();

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

    private void Record(HubPush push) => _pushes.Enqueue(push);

    private sealed class RecordingClient(RecordingHubContext hub) : IProjectHubClient
    {
        public async Task OutputReceived(string projectId, string rawJson)
        {
            if (hub._outputGate is { } gate) await gate.Task;
            hub.Record(new HubPush(nameof(OutputReceived), projectId, RawJson: rawJson));
        }

        public Task StatusChanged(string projectId, ProjectStatus status) =>
            Done(new HubPush(nameof(StatusChanged), projectId, status));

        public Task ProjectCreated(ProjectStatus status) => Done(new HubPush(nameof(ProjectCreated), status.Id, status));
        public Task CreationProgress(string projectId, string message) => Done(new HubPush(nameof(CreationProgress), projectId));
        public Task ProjectDeleted(string projectId) => Done(new HubPush(nameof(ProjectDeleted), projectId));
        public Task ProjectArchived(string projectId) => Done(new HubPush(nameof(ProjectArchived), projectId));
        public Task ProjectRestored(ProjectSummary project) => Done(new HubPush(nameof(ProjectRestored), project.Id));
        public Task ProfilesChanged() => Done(new HubPush(nameof(ProfilesChanged), null));

        private Task Done(HubPush push)
        {
            hub.Record(push);
            return Task.CompletedTask;
        }
    }

    /// <summary>Every address reaches the one recording client: the tests read what was pushed, not to whom.</summary>
    private sealed class AllClients(IProjectHubClient client) : IHubClients<IProjectHubClient>
    {
        public IProjectHubClient All => client;
        public IProjectHubClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => client;
        public IProjectHubClient Client(string connectionId) => client;
        public IProjectHubClient Clients(IReadOnlyList<string> connectionIds) => client;
        public IProjectHubClient Group(string groupName) => client;
        public IProjectHubClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => client;
        public IProjectHubClient Groups(IReadOnlyList<string> groupNames) => client;
        public IProjectHubClient User(string userId) => client;
        public IProjectHubClient Users(IReadOnlyList<string> userIds) => client;
    }

    private sealed class NoGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
