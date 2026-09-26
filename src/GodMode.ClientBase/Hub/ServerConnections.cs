using System.Collections.Concurrent;
using GodMode.ClientBase.Services;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using SignalR.Proxy;

namespace GodMode.ClientBase.Hub;

/// <summary>A server <see cref="ServerConnections"/> holds a connection to: its ID, and its name as last listed.</summary>
public sealed class ConnectedServer(string id, string name, string registrationId)
{
    public string Id { get; } = id;

    /// <summary>The registration that listed it (for a codespace, its GitHub account).</summary>
    public string RegistrationId { get; } = registrationId;

    public string Name { get; internal set; } = name;
}

/// <summary>What a <see cref="ServerConnections"/> does with each server's connection.</summary>
public interface IServerConnectionHandler
{
    /// <summary>A new connection, before it starts: register its callbacks (<c>connection.On</c>) here.</summary>
    void Configure(ConnectedServer server, HubConnection connection);

    /// <summary>The connection is up: take the server's state here. Throwing fails this connection, which is made again.</summary>
    Task OnConnectedAsync(ConnectedServer server, HubConnection connection, CancellationToken ct);

    /// <summary>The directory no longer lists the server: its connection is closed and not made again.</summary>
    void OnRemoved(string serverId);

    /// <summary>
    /// Every registration was listed, and these are all the servers there are. Not called when a listing failed:
    /// what is known of a server then may be that registration's.
    /// </summary>
    void OnListedCompletely(IReadOnlySet<string> serverIds);
}

/// <summary>
/// Holds one hub connection per server in the <see cref="IServerDirectory"/>, straight to the server (not through
/// the WebView's relay). A connection that fails or drops is made again, resolving the server afresh, so it picks the
/// server's best URL and its current token, waiting longer each time up to <c>maxRetryDelay</c>.
/// </summary>
public sealed class ServerConnections : IAsyncDisposable
{
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultMaxRetryDelay = TimeSpan.FromMinutes(1);

    private readonly IServerDirectory _directory;
    private readonly IServerConnectionHandler _handler;
    private readonly ILogger _logger;
    private readonly string _purpose;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _maxRetryDelay;
    private readonly ConcurrentDictionary<string, Watch> _watches = new();
    private readonly SemaphoreSlim _refreshing = new(1, 1);

    /// <param name="purpose">What the connections are for ("Attention", "Voice"), in the log.</param>
    public ServerConnections(IServerDirectory directory, IServerConnectionHandler handler, ILogger logger, string purpose,
        TimeSpan? retryDelay = null, TimeSpan? maxRetryDelay = null)
    {
        _directory = directory;
        _handler = handler;
        _logger = logger;
        _purpose = purpose;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
        _maxRetryDelay = maxRetryDelay ?? DefaultMaxRetryDelay;
    }

    /// <summary>The servers connected now, with their live connections.</summary>
    public IReadOnlyList<(ConnectedServer Server, HubConnection Connection)> Connected =>
        [.. _watches.Values
            .Select(w => (w.Server, w.Connection))
            .Where(c => c.Connection is { State: HubConnectionState.Connected })
            .Select(c => (c.Server, c.Connection!))];

    /// <summary>The server's live connection, or null while it has none.</summary>
    public HubConnection? ConnectionTo(string serverId) =>
        _watches.TryGetValue(serverId, out var watch) && watch.Connection is { State: HubConnectionState.Connected } connection
            ? connection
            : null;

    /// <summary>
    /// Connects to every server the directory lists now, and closes the connection to any that is gone: its
    /// registration was removed, or listed without it. A registration whose listing failed keeps its connections as
    /// they are. Call it on start and whenever the server list changes.
    /// </summary>
    /// <returns>How many servers it holds connections to now (connected or trying). None means it has nothing to do:
    /// no registration, or only ones that cannot be listed (a token secure storage cannot read, say).</returns>
    public async Task<int> RefreshAsync(CancellationToken ct = default)
    {
        await _refreshing.WaitAsync(ct);
        try
        {
            var listings = await _directory.ListByRegistrationAsync(ct);
            var failed = listings.Where(l => l.Servers is null).Select(l => l.RegistrationId).ToHashSet();
            foreach (var listing in listings.Where(l => l.Servers is not null))
            {
                foreach (var server in listing.Servers!)
                {
                    if (_watches.TryGetValue(server.Id, out var watch))
                        watch.Server.Name = server.Name;
                    else
                        _watches[server.Id] = Start(server, listing.RegistrationId);
                }
            }
            if (failed.Count > 0)
                _logger.LogWarning("{Purpose}: could not list the servers of {Count} registration(s); keeping their connections", _purpose, failed.Count);

            var listed = listings.SelectMany(l => l.Servers ?? []).Select(s => s.Id).ToHashSet();
            var kept = _watches.Values
                .Where(w => listed.Contains(w.Server.Id) || failed.Contains(w.Server.RegistrationId))
                .Select(w => w.Server.Id)
                .ToHashSet();
            foreach (var gone in _watches.Keys.Except(kept).ToList())
                await StopAsync(gone, removed: true);
            if (failed.Count == 0)
                _handler.OnListedCompletely(kept);
            return _watches.Count;
        }
        finally
        {
            _refreshing.Release();
        }
    }

    /// <summary>Drops every connection so each is made again at once, resolving its server afresh (the network changed).</summary>
    public void Reconnect()
    {
        foreach (var watch in _watches.Values)
            watch.Reconnect();
    }

    /// <summary>Closes every connection. The handler is not told the servers were removed: they were not.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var id in _watches.Keys.ToList())
            await StopAsync(id, removed: false);
    }

    private Watch Start(ServerInfo server, string registrationId)
    {
        _logger.LogInformation("{Purpose}: connecting to server {ServerId} ({Name})", _purpose, server.Id, server.Name);
        var watch = new Watch(new ConnectedServer(server.Id, server.Name, registrationId));
        watch.Loop = RunAsync(watch);
        return watch;
    }

    private async Task StopAsync(string serverId, bool removed)
    {
        if (!_watches.TryRemove(serverId, out var watch)) return;
        _logger.LogInformation("{Purpose}: no longer connected to server {ServerId}", _purpose, serverId);
        await watch.StopAsync();
        if (removed) _handler.OnRemoved(serverId);
    }

    private async Task RunAsync(Watch watch)
    {
        var stop = watch.Stopping;
        var delay = _retryDelay;
        while (!stop.IsCancellationRequested)
        {
            using var session = watch.NewSession();
            try
            {
                if (await _directory.ResolveAsync(watch.Server.Id, session.Token) is { } target)
                    await ListenAsync(watch, target, session.Token, onConnected: () => delay = _retryDelay);
                else
                    _logger.LogDebug("{Purpose}: server {ServerId} is unreachable; retrying in {Delay}", _purpose, watch.Server.Id, delay);
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested)
            {
                if (stop.IsCancellationRequested) return;
                // Reconnect(): make the connection again now
                delay = _retryDelay;
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("{Purpose}: connection to server {ServerId} failed: {Error}", _purpose, watch.Server.Id, ex.Message);
            }

            try
            {
                await Task.Delay(delay, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _maxRetryDelay.Ticks));
        }
    }

    /// <summary>Connects, lets the handler take the server's state, then holds the connection until it closes.</summary>
    private async Task ListenAsync(Watch watch, RelayTarget target, CancellationToken ct, Action onConnected)
    {
        await using var connection = HubConnections.Build(target);

        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += ex =>
        {
            closed.TrySetResult(ex);
            return Task.CompletedTask;
        };
        _handler.Configure(watch.Server, connection);

        await connection.StartAsync(ct);
        try
        {
            watch.Connection = connection;
            await _handler.OnConnectedAsync(watch.Server, connection, ct);
            _logger.LogInformation("{Purpose}: connection to server {ServerId} is up", _purpose, watch.Server.Id);
            onConnected();

            var error = await closed.Task.WaitAsync(ct);
            _logger.LogInformation("{Purpose}: connection to server {ServerId} closed: {Error}", _purpose, watch.Server.Id, error?.Message ?? "by the server");
        }
        finally
        {
            watch.Connection = null;
        }
    }

    private sealed class Watch(ConnectedServer server)
    {
        private readonly CancellationTokenSource _stop = new();
        private CancellationTokenSource? _session;

        public ConnectedServer Server { get; } = server;
        public Task Loop { get; set; } = Task.CompletedTask;
        public CancellationToken Stopping => _stop.Token;

        /// <summary>The live connection, while there is one.</summary>
        public volatile HubConnection? Connection;

        /// <summary>A token for one connection attempt: cancelled by <see cref="Reconnect"/> and by <see cref="StopAsync"/>.</summary>
        public CancellationTokenSource NewSession() =>
            _session = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);

        public void Reconnect()
        {
            try { _session?.Cancel(); }
            catch (ObjectDisposedException) { /* between sessions */ }
        }

        public async Task StopAsync()
        {
            await _stop.CancelAsync();
            await Loop;
            _stop.Dispose();
        }
    }
}
