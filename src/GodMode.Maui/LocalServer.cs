using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GodMode.ClientBase.Abstractions;
using GodMode.ClientBase.Services;
using GodMode.ClientBase.Services.Models;
using GodMode.Shared;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
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
    private HttpListener? _listener;
    private readonly List<StreamWriter> _sseClients = new();
    private readonly Lock _sseLock = new();

    public string BaseUrl { get; private set; } = "";

    public LocalServer(IServiceProvider services)
    {
        _services = services;
    }

    public void Start()
    {
        var port = FindFreePort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        BaseUrl = $"http://127.0.0.1:{port}";

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
            catch (Exception ex) { Console.WriteLine($"Listener error: {ex.Message}"); }
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
            Console.WriteLine($"Request error: {ex.Message}");
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
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }

        Log($"[Relay] WebSocket request for serverId={serverId}");
        var info = await ResolveHubUrlAsync(serverId);
        if (info == null)
        {
            Log($"[Relay] Could not resolve URL for serverId={serverId}");
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        Log($"[Relay] Connecting to {info.Value.Url} (token: {(info.Value.Token != null ? "yes" : "no")})");

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
        var relay = await SignalRRelay.ConnectAsync(wsContext.WebSocket, serverBuilder, msg => Log($"[Relay] {msg}"));

        if (relay == null) return;

        try
        {
            await relay.RunAsync();
        }
        finally
        {
            await relay.DisposeAsync();
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
            var servers = await provider.ListServersAsync();
            var server = servers.FirstOrDefault(s => s.Id == serverId);
            if (server != null)
            {
                var reg = registrations[serverIndex];
                var token = reg.Token != null ? registry.DecryptToken(reg.Token) : null;
                var baseUrl = (server.Url ?? reg.Url)?.TrimEnd('/');
                if (baseUrl == null) return null;
                return ($"{baseUrl}/hubs/projects", token);
            }
        }
        return null;
    }

    // ── REST API ─────────────────────────────────────────────────

    private async Task HandleRestAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "";
        var method = context.Request.HttpMethod;

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
                await HandleServerActionAsync(context, path, p => p.StartServerAsync);
                break;
            case (_, "POST") when path.StartsWith("/servers/") && path.EndsWith("/stop"):
                await HandleServerActionAsync(context, path, p => p.StopServerAsync);
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

        var result = list.Select((s, i) => new ServerRegistrationInfo(
            i.ToString(),
            s.DisplayName ?? s.Url ?? "Server",
            s.Url ?? "",
            "unknown"
        ));

        await WriteJsonAsync(context.Response, result);
    }

    private async Task HandleAddRegistrationAsync(HttpListenerContext context)
    {
        var req = await ReadJsonAsync<AddServerRequest>(context.Request);
        if (req == null)
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }

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

        var registry = _services.GetRequiredService<IServerRegistryService>();
        await registry.RemoveServerAsync(index);
        context.Response.StatusCode = 200;
        context.Response.Close();
        BroadcastEvent("serversChanged");
    }

    private async Task HandleListDiscoveredServersAsync(HttpListenerContext context)
    {
        var connections = _services.GetRequiredService<IServerConnectionService>();
        var servers = await connections.ListAllServersAsync();
        await WriteJsonAsync(context.Response, servers);
    }

    private async Task HandleServerActionAsync(
        HttpListenerContext context,
        string path,
        Func<IServerProvider, Func<string, Task>> getAction)
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

        var connections = _services.GetRequiredService<IServerConnectionService>();
        var providers = await connections.GetAllProvidersAsync();

        foreach (var (provider, _) in providers)
        {
            var servers = await provider.ListServersAsync();
            if (servers.Any(s => s.Id == serverId))
            {
                await getAction(provider)(serverId);
                context.Response.StatusCode = 200;
                context.Response.Close();
                BroadcastEvent("serversChanged");
                // Poll for state change in background (e.g., codespace starting up)
                _ = PollServerStateAsync(provider, serverId);
                return;
            }
        }

        context.Response.StatusCode = 404;
        context.Response.Close();
    }

    // ── Background state polling ────────────────────────────────

    private async Task PollServerStateAsync(IServerProvider provider, string serverId)
    {
        // Poll until the server reaches a stable state (Running or Stopped)
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(2000);
            try
            {
                var servers = await provider.ListServersAsync();
                var server = servers.FirstOrDefault(s => s.Id == serverId);
                if (server == null) break;

                BroadcastEvent("serversChanged");

                if (server.State is Shared.Enums.ServerState.Running or Shared.Enums.ServerState.Stopped)
                    break;
            }
            catch { break; }
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

        lock (_sseLock) _sseClients.Add(writer);

        try
        {
            // Keep connection open until client disconnects
            while (context.Response.OutputStream.CanWrite)
                await Task.Delay(5000);
        }
        catch { /* client disconnected */ }
        finally
        {
            lock (_sseLock) _sseClients.Remove(writer);
            try { writer.Dispose(); } catch { }
            try { context.Response.Close(); } catch { }
        }
    }

    private void BroadcastEvent(string eventType, object? data = null)
    {
        var json = data != null ? JsonSerializer.Serialize(data, JsonDefaults.Compact) : "{}";
        var message = $"event: {eventType}\ndata: {json}\n\n";

        lock (_sseLock)
        {
            var dead = new List<StreamWriter>();
            foreach (var writer in _sseClients)
            {
                try { writer.Write(message); }
                catch { dead.Add(writer); }
            }
            foreach (var w in dead)
            {
                _sseClients.Remove(w);
                try { w.Dispose(); } catch { }
            }
        }
    }

    // ── Logging ───────────────────────────────────────────────────

    private static void Log(string message)
    {
        System.Diagnostics.Debug.WriteLine(message);
        Console.WriteLine(message);
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
}
