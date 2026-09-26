using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace SignalR.Proxy;

/// <summary>
/// Where the relay connects for a server: the hub URL and the access token it adds.
/// The token stays in the host process; it is never sent to the client.
/// </summary>
public sealed record RelayTarget(string HubUrl, string? AccessToken);

/// <summary>
/// Loopback WebSocket relay (HttpListener) from the app's WebView to registered GodMode servers.
/// It serves nothing but the relay, and only to a caller that
/// <list type="bullet">
/// <item>sends an <c>Origin</c> from <c>allowedOrigins</c> (the WebView's own origin), else 403, and</item>
/// <item>presents the per-launch <see cref="Secret"/> as the <c>access_token</c> query parameter
/// (where SignalR's JS client puts it on a WebSocket URL), else 401.</item>
/// </list>
/// The relay target is resolved by server ID (<c>?serverId=</c>); an ID nothing resolves gets a 404.
/// </summary>
public sealed class LocalServer : IAsyncDisposable
{
    public const string SecretQueryKey = "access_token";
    public const string ServerIdQueryKey = "serverId";

    private readonly Func<string, CancellationToken, Task<RelayTarget?>> _resolve;
    private readonly HashSet<string> _allowedOrigins;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly byte[] _secretBytes;
    private readonly TimeSpan _handshakeTimeout;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _relays = new();
    private readonly CancellationTokenSource _stopping = new();
    private HttpListener? _listener;

    public string BaseUrl { get; private set; } = "";

    /// <summary>Random per launch. Only the WebView learns it, over the host bridge.</summary>
    public string Secret { get; }

    /// <summary>Relays connecting or running: each holds a client socket.</summary>
    public int ActiveRelayCount => _relays.Count;

    /// <param name="resolve">Resolves a server ID to its relay target, or null for an unknown or unreachable server.</param>
    /// <param name="allowedOrigins">Origins allowed to use the relay, e.g. <c>https://0.0.0.1</c>.</param>
    /// <param name="loggerFactory">Logs for the relay and each connection.</param>
    /// <param name="handshakeTimeout">How long a client may take to send its SignalR handshake, and a server to answer it
    /// (<see cref="SignalRRelay.DefaultHandshakeTimeout"/> when null).</param>
    public LocalServer(
        Func<string, CancellationToken, Task<RelayTarget?>> resolve,
        IEnumerable<string> allowedOrigins,
        ILoggerFactory loggerFactory,
        TimeSpan? handshakeTimeout = null)
    {
        _resolve = resolve;
        _allowedOrigins = new HashSet<string>(allowedOrigins.Select(o => o.TrimEnd('/')), StringComparer.OrdinalIgnoreCase);
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<LocalServer>();
        Secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        _secretBytes = Encoding.UTF8.GetBytes(Secret);
        _handshakeTimeout = handshakeTimeout ?? SignalRRelay.DefaultHandshakeTimeout;
    }

    public void Start()
    {
        var port = FindFreePort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        BaseUrl = $"http://127.0.0.1:{port}";
        _logger.LogInformation("Listening on {BaseUrl}", BaseUrl);

        _ = Task.Run(ListenLoopAsync);
    }

    /// <summary>
    /// Closes every active relay. The clients reconnect, and each reconnect picks the server's URL afresh
    /// (used on a network change).
    /// </summary>
    public void DropAllRelays()
    {
        _logger.LogInformation("Dropping {Count} active relays", _relays.Count);
        foreach (var cts in _relays.Values)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* that relay ended meanwhile */ }
        }
    }

    private static int FindFreePort()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    private async Task ListenLoopAsync()
    {
        while (_listener?.IsListening == true)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context));
            }
            catch (Exception) when (_listener?.IsListening != true) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Listener error"); }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            if (Reject(context.Request) is { } status)
            {
                context.Response.StatusCode = status;
                context.Response.Close();
                return;
            }
            await HandleWebSocketAsync(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Request error: {Method} {Path}", context.Request.HttpMethod,
                context.Request.Url?.AbsolutePath);
            try { context.Response.StatusCode = 500; context.Response.Close(); } catch { /* already closed */ }
        }
    }

    /// <summary>The status to refuse a request with, or null to relay it.</summary>
    private int? Reject(HttpListenerRequest request)
    {
        var origin = request.Headers["Origin"];
        if (origin == null || !_allowedOrigins.Contains(origin.TrimEnd('/')))
        {
            _logger.LogWarning("Refused {Method} {Path}: origin {Origin} not allowed", request.HttpMethod,
                request.Url?.AbsolutePath, origin ?? "(none)");
            return 403;
        }

        var presented = request.QueryString[SecretQueryKey];
        if (presented == null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _secretBytes))
        {
            _logger.LogWarning("Refused {Method} {Path}: missing or wrong relay secret", request.HttpMethod,
                request.Url?.AbsolutePath);
            return 401;
        }

        if (!request.IsWebSocketRequest || request.Url?.AbsolutePath != "/")
            return 404;

        return string.IsNullOrEmpty(request.QueryString[ServerIdQueryKey]) ? 400 : null;
    }

    private async Task HandleWebSocketAsync(HttpListenerContext context)
    {
        var serverId = context.Request.QueryString[ServerIdQueryKey]!;
        _logger.LogInformation("WebSocket relay request for serverId={ServerId}", serverId);

        var target = await _resolve(serverId, _stopping.Token);
        if (target == null)
        {
            _logger.LogWarning("No relay target for serverId={ServerId}", serverId);
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        _logger.LogInformation("Relay connecting to {Url} (token: {HasToken})", target.HubUrl,
            target.AccessToken != null ? "yes" : "no");

        var serverBuilder = new HubConnectionBuilder()
            .WithUrl(target.HubUrl, options =>
            {
                if (target.AccessToken != null)
                    options.AccessTokenProvider = () => Task.FromResult<string?>(target.AccessToken);
            });

        var wsContext = await context.AcceptWebSocketAsync(null);

        // Counted from here, so DropAllRelays and DisposeAsync also end a relay that is still connecting
        var relayId = Guid.NewGuid().ToString("N");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        _relays[relayId] = cts;
        try
        {
            await using var relay = await SignalRRelay.ConnectAsync(wsContext.WebSocket, serverBuilder,
                _loggerFactory.CreateLogger<SignalRRelay>(), _handshakeTimeout, cts.Token);
            if (relay == null)
            {
                _logger.LogWarning("Relay failed to connect for serverId={ServerId}", serverId);
                return;
            }

            _logger.LogInformation("Relay active for serverId={ServerId} (total active: {Count})", serverId, _relays.Count);
            await relay.RunAsync(cts.Token);
        }
        finally
        {
            _relays.TryRemove(relayId, out _);
            _logger.LogInformation("Relay closed for serverId={ServerId} (total active: {Count})", serverId, _relays.Count);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        try { _listener?.Stop(); } catch { /* already stopped */ }
        _listener?.Close();
    }
}
