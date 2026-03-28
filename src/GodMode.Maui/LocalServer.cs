using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GodMode.ClientBase.Abstractions;
using GodMode.ClientBase.Services;
using GodMode.ClientBase.Services.Models;
using GodMode.Shared;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SignalR.Proxy;

namespace GodMode.Maui;

/// <summary>
/// Lightweight HTTP + WebSocket server using HttpListener.
/// Serves REST endpoints for server management and relays
/// WebSocket connections to remote GodMode.Server instances.
/// </summary>
public class LocalServer
{
    private readonly IServiceProvider _services;
    private readonly ILogger<LocalServer> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private HttpListener? _listener;
    private readonly List<StreamWriter> _sseClients = new();
    private readonly Lock _sseLock = new();
    private int _activeRelays;

    public string BaseUrl { get; private set; } = "";

    public LocalServer(IServiceProvider services)
    {
        _services = services;
        _loggerFactory = services.GetRequiredService<ILoggerFactory>();
        _logger = _loggerFactory.CreateLogger<LocalServer>();
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
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Listener error"); }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            AddCorsHeaders(context.Response);

            if (context.Request.HttpMethod == "OPTIONS")
            {
                context.Response.StatusCode = 204;
                context.Response.Close();
                return;
            }

            if (context.Request.IsWebSocketRequest)
                await HandleWebSocketAsync(context);
            else if (context.Request.Url?.AbsolutePath == "/events" && context.Request.HttpMethod == "GET")
                await HandleSseAsync(context);
            else
                await HandleRestAsync(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Request error: {Method} {Path}", context.Request.HttpMethod,
                context.Request.Url?.AbsolutePath);
            try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
        }
    }

    private static void AddCorsHeaders(HttpListenerResponse response)
    {
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
    }

    // ── WebSocket relay ──────────────────────────────────────────

    private async Task HandleWebSocketAsync(HttpListenerContext context)
    {
        var serverId = context.Request.QueryString["serverId"];
        if (string.IsNullOrEmpty(serverId))
        {
            _logger.LogWarning("WebSocket request missing serverId");
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }

        _logger.LogInformation("WebSocket relay request for serverId={ServerId}", serverId);
        var info = await ResolveHubUrlAsync(serverId);
        if (info == null)
        {
            _logger.LogWarning("Could not resolve hub URL for serverId={ServerId}", serverId);
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        _logger.LogInformation("Relay connecting to {Url} (token: {HasToken})", info.Value.Url,
            info.Value.Token != null ? "yes" : "no");

        var serverBuilder = new HubConnectionBuilder()
            .WithUrl(info.Value.Url, options =>
            {
                if (info.Value.Token != null)
                    options.AccessTokenProvider = () => Task.FromResult<string?>(info.Value.Token);
            })
            .AddJsonProtocol(options =>
            {
                var defaults = JsonDefaults.Options;
                options.PayloadSerializerOptions.PropertyNamingPolicy = defaults.PropertyNamingPolicy;
                options.PayloadSerializerOptions.DefaultIgnoreCondition = defaults.DefaultIgnoreCondition;
                foreach (var converter in defaults.Converters)
                    options.PayloadSerializerOptions.Converters.Add(converter);
            });

        var wsContext = await context.AcceptWebSocketAsync(null);
        var relayLogger = _loggerFactory.CreateLogger<SignalRRelay>();
        var relay = await SignalRRelay.ConnectAsync(wsContext.WebSocket, serverBuilder, relayLogger);

        if (relay == null)
        {
            _logger.LogWarning("Relay failed to connect for serverId={ServerId}", serverId);
            return;
        }

        var count = Interlocked.Increment(ref _activeRelays);
        _logger.LogInformation("Relay active for serverId={ServerId} (total active: {Count})", serverId, count);

        try
        {
            await relay.RunAsync();
        }
        finally
        {
            await relay.DisposeAsync();
            count = Interlocked.Decrement(ref _activeRelays);
            _logger.LogInformation("Relay closed for serverId={ServerId} (total active: {Count})", serverId, count);
        }
    }

    private async Task<(string Url, string? Token)?> ResolveHubUrlAsync(string serverId)
    {
        var registry = _services.GetRequiredService<IServerRegistryService>();
        var connections = _services.GetRequiredService<IServerConnectionService>();

        var providers = await connections.GetAllProvidersAsync();
        var registrations = await registry.GetServersAsync();

        foreach (var (provider, serverIndex) in providers)
        {
            var servers = await provider.ListHostsAsync();
            var server = servers.FirstOrDefault(s => s.Id == serverId);
            if (server != null)
            {
                var reg = registrations[serverIndex];
                var token = reg.Token != null ? registry.DecryptToken(reg.Token) : null;
                var baseUrl = (server.Url ?? reg.Url)?.TrimEnd('/');
                if (baseUrl == null)
                {
                    _logger.LogWarning("Server {ServerId} has no URL", serverId);
                    return null;
                }
                var hubUrl = $"{baseUrl}/hubs/projects";
                _logger.LogDebug("Resolved {ServerId} -> {HubUrl}", serverId, hubUrl);
                return (hubUrl, token);
            }
        }
        _logger.LogWarning("Server {ServerId} not found in any provider", serverId);
        return null;
    }

    // ── REST API ─────────────────────────────────────────────────

    private async Task HandleRestAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "";
        var method = context.Request.HttpMethod;

        _logger.LogDebug("REST {Method} {Path}", method, path);

        switch (path, method)
        {
            case ("/servers/registrations", "GET"):
                await HandleListRegistrationsAsync(context);
                break;
            case ("/servers/registrations", "POST"):
                await HandleAddRegistrationAsync(context);
                break;
            case (_, "DELETE") when path.StartsWith("/servers/registrations/"):
                await HandleRemoveRegistrationAsync(context, path);
                break;
            case ("/servers", "GET"):
                await HandleListDiscoveredServersAsync(context);
                break;
            case (_, "POST") when path.StartsWith("/servers/") && path.EndsWith("/start"):
                await HandleServerActionAsync(context, path, p => p.StartHostAsync, "start");
                break;
            case (_, "POST") when path.StartsWith("/servers/") && path.EndsWith("/stop"):
                await HandleServerActionAsync(context, path, p => p.StopHostAsync, "stop");
                break;
            case ("/devtools", "POST"):
                MainPage.OpenDevTools();
                context.Response.StatusCode = 200;
                context.Response.Close();
                break;
            default:
                context.Response.StatusCode = 404;
                context.Response.Close();
                break;
        }
    }

    private async Task HandleListRegistrationsAsync(HttpListenerContext context)
    {
        var registry = _services.GetRequiredService<IServerRegistryService>();
        var list = await registry.GetServersAsync();

        var result = list.Select((s, i) => new
        {
            Id = i.ToString(),
            DisplayName = s.DisplayName ?? s.Url ?? "Server",
            Url = s.Url ?? "",
            ConnectionState = "unknown"
        });

        _logger.LogDebug("GET /servers/registrations -> {Count} registrations", list.Count);
        await WriteJsonAsync(context.Response, result);
    }

    private async Task HandleAddRegistrationAsync(HttpListenerContext context)
    {
        var req = await ReadJsonAsync<AddHostRequest>(context.Request);
        if (req == null)
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }

        _logger.LogInformation("Adding server registration: type={Type} name={Name}", req.Type, req.DisplayName);
        var registry = _services.GetRequiredService<IServerRegistryService>();
        var registration = new ServerRegistration
        {
            Type = req.Type,
            Url = req.Url,
            DisplayName = req.DisplayName,
            Username = req.Username,
            Token = !string.IsNullOrEmpty(req.AccessToken)
                ? registry.EncryptToken(req.AccessToken)
                : null
        };
        await registry.AddServerAsync(registration);
        context.Response.StatusCode = 201;
        context.Response.Close();
        BroadcastEvent("serversChanged");
    }

    private async Task HandleRemoveRegistrationAsync(HttpListenerContext context, string path)
    {
        var indexStr = path["/servers/registrations/".Length..];
        if (!int.TryParse(indexStr, out var index))
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }

        _logger.LogInformation("Removing server registration at index {Index}", index);
        var registry = _services.GetRequiredService<IServerRegistryService>();
        await registry.RemoveServerAsync(index);
        context.Response.StatusCode = 200;
        context.Response.Close();
        BroadcastEvent("serversChanged");
    }

    private async Task HandleListDiscoveredServersAsync(HttpListenerContext context)
    {
        _logger.LogDebug("GET /servers — discovering servers...");
        var connections = _services.GetRequiredService<IServerConnectionService>();
        var servers = (await connections.ListAllHostsAsync()).ToList();
        _logger.LogInformation("GET /servers -> {Count} servers: [{Servers}]", servers.Count,
            string.Join(", ", servers.Select(s => $"{s.Name}({s.State})")));
        await WriteJsonAsync(context.Response, servers);
    }

    private async Task HandleServerActionAsync(
        HttpListenerContext context,
        string path,
        Func<IHostProvider, Func<string, Task>> getAction,
        string actionName)
    {
        // Path: /servers/{serverId}/start or /servers/{serverId}/stop
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3)
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }
        var serverId = Uri.UnescapeDataString(segments[1]);

        _logger.LogInformation("Server action: {Action} on {ServerId}", actionName, serverId);
        var connections = _services.GetRequiredService<IServerConnectionService>();
        var providers = await connections.GetAllProvidersAsync();

        foreach (var (provider, _) in providers)
        {
            var servers = await provider.ListHostsAsync();
            if (servers.Any(s => s.Id == serverId))
            {
                await getAction(provider)(serverId);
                context.Response.StatusCode = 200;
                context.Response.Close();
                _logger.LogInformation("Server {Action} initiated for {ServerId}, starting poll", actionName, serverId);
                BroadcastEvent("serversChanged");
                _ = PollServerStateAsync(provider, serverId);
                return;
            }
        }

        _logger.LogWarning("Server {ServerId} not found for {Action}", serverId, actionName);
        context.Response.StatusCode = 404;
        context.Response.Close();
    }

    // ── Background state polling ────────────────────────────────

    private async Task PollServerStateAsync(IHostProvider provider, string serverId)
    {
        _logger.LogDebug("Poll started for {ServerId}", serverId);
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(2000);
            try
            {
                var servers = await provider.ListHostsAsync();
                var server = servers.FirstOrDefault(s => s.Id == serverId);
                if (server == null)
                {
                    _logger.LogDebug("Poll: {ServerId} no longer found, stopping", serverId);
                    break;
                }

                _logger.LogDebug("Poll #{Iteration}: {ServerId} state={State}", i + 1, serverId, server.State);
                BroadcastEvent("serversChanged");

                if (server.State is Shared.Enums.HostState.Running or Shared.Enums.HostState.Stopped)
                {
                    _logger.LogInformation("Poll: {ServerId} reached stable state {State}", serverId, server.State);
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Poll error for {ServerId}", serverId);
                break;
            }
        }
    }

    // ── Server-Sent Events ────────────────────────────────────────

    private async Task HandleSseAsync(HttpListenerContext context)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.Add("Cache-Control", "no-cache");
        context.Response.Headers.Add("Connection", "keep-alive");
        context.Response.StatusCode = 200;

        var writer = new StreamWriter(context.Response.OutputStream, Encoding.UTF8) { AutoFlush = true };
        await writer.WriteLineAsync(": connected\n");

        int clientCount;
        lock (_sseLock)
        {
            _sseClients.Add(writer);
            clientCount = _sseClients.Count;
        }
        _logger.LogInformation("SSE client connected (total: {Count})", clientCount);

        try
        {
            while (context.Response.OutputStream.CanWrite)
                await Task.Delay(5000);
        }
        catch { /* client disconnected */ }
        finally
        {
            lock (_sseLock)
            {
                _sseClients.Remove(writer);
                clientCount = _sseClients.Count;
            }
            _logger.LogInformation("SSE client disconnected (total: {Count})", clientCount);
            try { writer.Dispose(); } catch { }
            try { context.Response.Close(); } catch { }
        }
    }

    private void BroadcastEvent(string eventType, object? data = null)
    {
        var json = data != null ? JsonSerializer.Serialize(data, JsonDefaults.Compact) : "{}";
        var message = $"event: {eventType}\ndata: {json}\n\n";

        int sent = 0, failed = 0;
        lock (_sseLock)
        {
            var dead = new List<StreamWriter>();
            foreach (var writer in _sseClients)
            {
                try { writer.Write(message); sent++; }
                catch { dead.Add(writer); failed++; }
            }
            foreach (var w in dead)
            {
                _sseClients.Remove(w);
                try { w.Dispose(); } catch { }
            }
        }
        _logger.LogDebug("SSE broadcast: {EventType} (sent={Sent}, failed={Failed})", eventType, sent, failed);
    }

    // ── JSON helpers ─────────────────────────────────────────────

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest request) where T : class
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var body = await reader.ReadToEndAsync();
        return JsonSerializer.Deserialize<T>(body, JsonDefaults.Compact);
    }

    private static async Task WriteJsonAsync<T>(HttpListenerResponse response, T value)
    {
        response.ContentType = "application/json";
        var json = JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Compact);
        response.ContentLength64 = json.Length;
        await response.OutputStream.WriteAsync(json);
        response.Close();
    }

    private record AddHostRequest(
        string DisplayName,
        string Url,
        string? AccessToken = null,
        string Type = "local",
        string? Username = null
    );
}
