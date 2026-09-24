using System.Collections.Concurrent;
using GodMode.ClientBase.Services;
using GodMode.Shared;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SignalR.Proxy;

namespace GodMode.ClientBase.Attention;

/// <summary>
/// Holds one hub connection per server in the <see cref="IServerDirectory"/>, straight to the server (not through
/// the WebView's relay), and keeps the <see cref="IAttentionNotifier"/> showing what each one's attention list holds.
/// A connection that fails or drops is made again, resolving the server afresh, so it picks the server's best URL
/// and its current token; until then, what was shown for that server stays shown.
/// </summary>
public sealed class AttentionWatcher : IAsyncDisposable
{
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultMaxRetryDelay = TimeSpan.FromMinutes(1);

    private readonly IServerDirectory _directory;
    private readonly AttentionTracker _tracker;
    private readonly ILogger _logger;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _maxRetryDelay;
    private readonly ConcurrentDictionary<string, Watch> _watches = new();
    private readonly SemaphoreSlim _refreshing = new(1, 1);

    public AttentionWatcher(IServerDirectory directory, IAttentionNotifier notifier, ILoggerFactory loggerFactory,
        TimeSpan? retryDelay = null, TimeSpan? maxRetryDelay = null)
    {
        _directory = directory;
        _tracker = new AttentionTracker(notifier);
        _logger = loggerFactory.CreateLogger<AttentionWatcher>();
        _retryDelay = retryDelay ?? DefaultRetryDelay;
        _maxRetryDelay = maxRetryDelay ?? DefaultMaxRetryDelay;
    }

    /// <summary>
    /// Watches every server the directory lists now, and stops watching (and cancels what was shown for) any it no
    /// longer lists. Call it on start and whenever the server list changes.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _refreshing.WaitAsync(ct);
        try
        {
            var servers = await _directory.ListAllServersAsync(ct);
            foreach (var server in servers)
            {
                if (_watches.TryGetValue(server.Id, out var watch))
                    watch.Name = server.Name;
                else
                    _watches[server.Id] = Start(server);
            }
            foreach (var gone in _watches.Keys.Except(servers.Select(s => s.Id)).ToList())
                await StopAsync(gone);
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

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _watches.Keys.ToList())
            await StopAsync(id, cancelShown: false);
    }

    private Watch Start(ServerInfo server)
    {
        _logger.LogInformation("Watching attention on server {ServerId} ({Name})", server.Id, server.Name);
        var watch = new Watch(server.Id, server.Name);
        watch.Loop = RunAsync(watch);
        return watch;
    }

    private async Task StopAsync(string serverId, bool cancelShown = true)
    {
        if (!_watches.TryRemove(serverId, out var watch)) return;
        _logger.LogInformation("No longer watching server {ServerId}", serverId);
        await watch.StopAsync();
        if (cancelShown) _tracker.Remove(serverId);
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
                if (await _directory.ResolveAsync(watch.Id, session.Token) is { } target)
                    await ListenAsync(watch, target, session.Token, onConnected: () => delay = _retryDelay);
                else
                    _logger.LogDebug("Server {ServerId} is unreachable; retrying in {Delay}", watch.Id, delay);
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
                _logger.LogWarning("Attention connection to server {ServerId} failed: {Error}", watch.Id, ex.Message);
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

    /// <summary>Connects, takes the whole list, then follows AttentionChanged until the connection closes.</summary>
    private async Task ListenAsync(Watch watch, RelayTarget target, CancellationToken ct, Action onConnected)
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(target.HubUrl, options =>
            {
                if (target.AccessToken != null)
                    options.AccessTokenProvider = () => Task.FromResult<string?>(target.AccessToken);
            })
            .AddJsonProtocol(options =>
            {
                // The server's payload conventions: PascalCase, string enums
                var defaults = JsonDefaults.Options;
                options.PayloadSerializerOptions.PropertyNamingPolicy = defaults.PropertyNamingPolicy;
                options.PayloadSerializerOptions.DefaultIgnoreCondition = defaults.DefaultIgnoreCondition;
                foreach (var converter in defaults.Converters)
                    options.PayloadSerializerOptions.Converters.Add(converter);
            })
            .Build();

        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += ex =>
        {
            closed.TrySetResult(ex);
            return Task.CompletedTask;
        };
        connection.On<AttentionItem[]>(nameof(IProjectHubClient.AttentionChanged),
            items => _tracker.Update(watch.Id, watch.Name, items));

        await connection.StartAsync(ct);
        _tracker.Update(watch.Id, watch.Name, await connection.InvokeAsync<AttentionItem[]>(nameof(IProjectHub.GetAttention), ct));
        _logger.LogInformation("Attention connection to server {ServerId} is up", watch.Id);
        onConnected();

        var error = await closed.Task.WaitAsync(ct);
        _logger.LogInformation("Attention connection to server {ServerId} closed: {Error}", watch.Id, error?.Message ?? "by the server");
    }

    private sealed class Watch(string id, string name)
    {
        private readonly CancellationTokenSource _stop = new();
        private CancellationTokenSource? _session;

        public string Id { get; } = id;
        public string Name { get; set; } = name;
        public Task Loop { get; set; } = Task.CompletedTask;
        public CancellationToken Stopping => _stop.Token;

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
