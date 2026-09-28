using GodMode.ClientBase.Hub;
using GodMode.ClientBase.Services;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GodMode.ClientBase.Attention;

/// <summary>
/// Holds one hub connection per server in the <see cref="IServerDirectory"/> (<see cref="ServerConnections"/>), and
/// keeps the <see cref="IAttentionNotifier"/> showing what each one's attention list holds. While a server's
/// connection is being made again, what was shown for that server stays shown.
/// </summary>
public sealed class AttentionWatcher : IAsyncDisposable
{
    public static readonly TimeSpan DefaultRetryDelay = ServerConnections.DefaultRetryDelay;
    public static readonly TimeSpan DefaultMaxRetryDelay = ServerConnections.DefaultMaxRetryDelay;

    private readonly ServerConnections _connections;

    public AttentionWatcher(IServerDirectory directory, IAttentionNotifier notifier, ILoggerFactory loggerFactory,
        TimeSpan? retryDelay = null, TimeSpan? maxRetryDelay = null)
    {
        _connections = new ServerConnections(directory, new Handler(new AttentionTracker(notifier)),
            loggerFactory.CreateLogger<AttentionWatcher>(), "Attention", retryDelay, maxRetryDelay);
    }

    /// <summary>
    /// Watches every server the directory lists now, and stops watching (and cancels what was shown for) any that is
    /// gone: its registration was removed, or listed without it. A registration whose listing failed keeps its
    /// watches as they are. Call it on start and whenever the server list changes.
    /// </summary>
    /// <returns>How many servers it watches now. None means it has nothing to do: no registration, or only ones that
    /// cannot be listed (a token secure storage cannot read, say).</returns>
    public Task<int> RefreshAsync(CancellationToken ct = default) => _connections.RefreshAsync(ct);

    /// <summary>Drops every connection so each is made again at once, resolving its server afresh (the network changed).</summary>
    public void Reconnect() => _connections.Reconnect();

    public ValueTask DisposeAsync() => _connections.DisposeAsync();

    /// <summary>Takes the whole list on each connection, then follows AttentionChanged.</summary>
    private sealed class Handler(AttentionTracker tracker) : IServerConnectionHandler
    {
        public void Configure(ConnectedServer server, HubConnection connection) =>
            connection.On<AttentionItem[]>(nameof(IProjectHubClient.AttentionChanged),
                items => tracker.Update(server.Id, server.Name, items));

        public async Task OnConnectedAsync(ConnectedServer server, HubConnection connection, CancellationToken ct) =>
            tracker.Update(server.Id, server.Name, await connection.InvokeAsync<AttentionItem[]>(nameof(IProjectHub.GetAttention), ct));

        public void OnRemoved(string serverId) => tracker.Remove(serverId);

        // What an earlier run showed is known by server alone: with a listing missing, it may be that registration's
        public void OnListedCompletely(IReadOnlySet<string> serverIds) => tracker.RemoveAllExcept(serverIds);
    }
}
