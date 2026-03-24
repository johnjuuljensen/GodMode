using System.Collections.Concurrent;
using GodMode.Shared;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using TypedSignalR.Client;

namespace GodMode.Maui.Services;

/// <summary>
/// Manages outbound SignalR connections to remote GodMode.Server instances.
/// Each server gets its own HubConnection. Callbacks from remote servers
/// are forwarded to local React clients in the appropriate group.
/// </summary>
public class ServerConnectionManager
{
    private readonly ConcurrentDictionary<string, ManagedServer> _servers = new();
    private readonly IHubContext<Hubs.GodModeLocalHub, IProjectHubClient> _hubContext;
    private readonly string _registryPath;

    public ServerConnectionManager(IHubContext<Hubs.GodModeLocalHub, IProjectHubClient> hubContext)
    {
        _hubContext = hubContext;
        _registryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GodMode", "servers.json");
    }

    public ServerInfo[] ListServers() =>
        _servers.Values.Select(s => s.ToServerInfo()).ToArray();

    public ServerInfo AddServer(AddServerRequest request)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var server = new ManagedServer(id, request.DisplayName, request.Url, request.AccessToken);
        _servers[id] = server;
        SaveRegistry();
        return server.ToServerInfo();
    }

    public void UpdateServer(string serverId, AddServerRequest request)
    {
        if (!_servers.TryGetValue(serverId, out var existing))
            throw new KeyNotFoundException($"Server '{serverId}' not found");

        if (existing.IsConnected)
            throw new InvalidOperationException("Disconnect before updating");

        _servers[serverId] = new ManagedServer(serverId, request.DisplayName, request.Url, request.AccessToken);
        SaveRegistry();
    }

    public async Task RemoveServer(string serverId)
    {
        if (_servers.TryRemove(serverId, out var server))
        {
            await server.DisconnectAsync();
            SaveRegistry();
        }
    }

    public async Task ConnectServer(string serverId)
    {
        if (!_servers.TryGetValue(serverId, out var server))
            throw new KeyNotFoundException($"Server '{serverId}' not found");

        await server.ConnectAsync(_hubContext, serverId);
    }

    public async Task DisconnectServer(string serverId)
    {
        if (_servers.TryGetValue(serverId, out var server))
            await server.DisconnectAsync();
    }

    public IProjectHub? GetHubProxy(string serverId) =>
        _servers.TryGetValue(serverId, out var server) && server.IsConnected
            ? server.HubProxy
            : null;

    public void LoadRegistry()
    {
        if (!File.Exists(_registryPath)) return;

        try
        {
            var json = File.ReadAllText(_registryPath);
            var entries = System.Text.Json.JsonSerializer.Deserialize<ServerEntry[]>(json, JsonDefaults.Options);
            if (entries is null) return;

            foreach (var entry in entries)
                _servers[entry.Id] = new ManagedServer(entry.Id, entry.DisplayName, entry.Url, entry.AccessToken);
        }
        catch { /* corrupt file, start fresh */ }
    }

    private void SaveRegistry()
    {
        var dir = Path.GetDirectoryName(_registryPath)!;
        Directory.CreateDirectory(dir);

        var entries = _servers.Values.Select(s => new ServerEntry(s.Id, s.DisplayName, s.Url, s.AccessToken)).ToArray();
        var json = System.Text.Json.JsonSerializer.Serialize(entries, JsonDefaults.Options);
        File.WriteAllText(_registryPath, json);
    }

    private record ServerEntry(string Id, string DisplayName, string Url, string? AccessToken);
}

/// <summary>
/// Holds the state for a single registered server: metadata + optional active connection.
/// </summary>
internal class ManagedServer
{
    public string Id { get; }
    public string DisplayName { get; }
    public string Url { get; }
    public string? AccessToken { get; }

    private HubConnection? _hubConnection;
    private IDisposable? _clientRegistration;

    public IProjectHub? HubProxy { get; private set; }
    public bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

    public ManagedServer(string id, string displayName, string url, string? accessToken)
    {
        Id = id;
        DisplayName = displayName;
        Url = url;
        AccessToken = accessToken;
    }

    public async Task ConnectAsync(IHubContext<Hubs.GodModeLocalHub, IProjectHubClient> hubContext, string serverId)
    {
        if (IsConnected) return;

        var hubUrl = Url.TrimEnd('/') + "/hubs/projects";
        var builder = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                if (!string.IsNullOrEmpty(AccessToken))
                    options.AccessTokenProvider = () => Task.FromResult<string?>(AccessToken);
            })
            .WithAutomaticReconnect()
            .AddJsonProtocol(options =>
            {
                var defaults = JsonDefaults.Options;
                options.PayloadSerializerOptions.PropertyNamingPolicy = defaults.PropertyNamingPolicy;
                options.PayloadSerializerOptions.DefaultIgnoreCondition = defaults.DefaultIgnoreCondition;
                foreach (var converter in defaults.Converters)
                    options.PayloadSerializerOptions.Converters.Add(converter);
            });

        _hubConnection = builder.Build();
        HubProxy = _hubConnection.CreateHubProxy<IProjectHub>();

        // Forward remote callbacks to local React clients in this server's group
        var forwarder = new CallbackForwarder(hubContext, serverId);
        _clientRegistration = _hubConnection.Register<IProjectHubClient>(forwarder);

        await _hubConnection.StartAsync();
    }

    public async Task DisconnectAsync()
    {
        _clientRegistration?.Dispose();
        _clientRegistration = null;
        HubProxy = null;

        if (_hubConnection is not null)
        {
            await _hubConnection.DisposeAsync();
            _hubConnection = null;
        }
    }

    public ServerInfo ToServerInfo() => new(
        Id,
        DisplayName,
        Url,
        IsConnected ? "connected" : "disconnected"
    );
}

/// <summary>
/// Receives IProjectHubClient callbacks from a remote server and forwards
/// them to all local hub clients in the server's group.
/// </summary>
internal class CallbackForwarder(
    IHubContext<Hubs.GodModeLocalHub, IProjectHubClient> hubContext,
    string serverId) : IProjectHubClient
{
    private IProjectHubClient Group => hubContext.Clients.Group($"server-{serverId}");

    public Task OutputReceived(string projectId, string rawJson) =>
        Group.OutputReceived(projectId, rawJson);

    public Task StatusChanged(string projectId, ProjectStatus status) =>
        Group.StatusChanged(projectId, status);

    public Task ProjectCreated(ProjectStatus status) =>
        Group.ProjectCreated(status);

    public Task CreationProgress(string projectId, string message) =>
        Group.CreationProgress(projectId, message);

    public Task ProjectDeleted(string projectId) =>
        Group.ProjectDeleted(projectId);
}
