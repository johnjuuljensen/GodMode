using System.Collections.Concurrent;
using GodMode.ClientBase.Providers;
using GodMode.ClientBase.Services;
using GodMode.ClientBase.Services.Models;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.Maui.Services;

/// <summary>
/// Manages outbound SignalR connections to remote GodMode.Server instances.
/// Delegates persistence to IServerRegistryService (ClientBase) and connection
/// building to HubConnectionFactory. Adds proxy-specific callback forwarding.
/// </summary>
public class ServerConnectionManager
{
    private readonly IServerRegistryService _registry;
    private readonly IHubContext<Hubs.GodModeLocalHub> _hubContext;
    private readonly ConcurrentDictionary<string, HubConnection> _connections = new();

    public ServerConnectionManager(IServerRegistryService registry, IHubContext<Hubs.GodModeLocalHub> hubContext)
    {
        _registry = registry;
        _hubContext = hubContext;
    }

    public HubConnection? GetRemoteConnection(string serverId)
    {
        _connections.TryGetValue(serverId, out var conn);
        return conn?.State == HubConnectionState.Connected ? conn : null;
    }

    public async Task<ServerInfo[]> ListServersAsync()
    {
        var servers = await _registry.GetServersAsync();
        return servers.Select((s, i) => ToServerInfo(s, i)).ToArray();
    }

    public async Task<ServerInfo> AddServerAsync(AddServerRequest request)
    {
        var registration = new ServerRegistration
        {
            Type = "local",
            Url = request.Url,
            DisplayName = request.DisplayName,
            Token = request.AccessToken
        };
        await _registry.AddServerAsync(registration);

        var servers = await _registry.GetServersAsync();
        return ToServerInfo(servers[^1], servers.Count - 1);
    }

    public async Task RemoveServerAsync(int index)
    {
        var serverId = index.ToString();
        if (_connections.TryRemove(serverId, out var conn))
            await conn.DisposeAsync();
        await _registry.RemoveServerAsync(index);
    }

    public async Task ConnectServerAsync(int index)
    {
        var serverId = index.ToString();
        var servers = await _registry.GetServersAsync();
        if (index < 0 || index >= servers.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        var server = servers[index];
        var accessToken = !string.IsNullOrEmpty(server.Token)
            ? _registry.DecryptToken(server.Token)
            : null;

        var connection = await HubConnectionFactory.CreateAndStartAsync(
            server.Url ?? "http://localhost:31337", accessToken);

        // Forward all IProjectHubClient callbacks to local React clients
        foreach (var method in typeof(IProjectHubClient).GetMethods())
        {
            var methodName = method.Name;
            var paramTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();
            connection.On(methodName, paramTypes, args =>
                _hubContext.Clients.Group($"server-{serverId}").SendCoreAsync(methodName, args));
        }

        _connections[serverId] = connection;
    }

    public async Task DisconnectServerAsync(int index)
    {
        var serverId = index.ToString();
        if (_connections.TryRemove(serverId, out var conn))
            await conn.DisposeAsync();
    }

    private ServerInfo ToServerInfo(ServerRegistration reg, int index)
    {
        var serverId = index.ToString();
        var isConnected = _connections.TryGetValue(serverId, out var c) && c.State == HubConnectionState.Connected;
        return new ServerInfo(serverId, reg.DisplayName ?? reg.Url ?? "Server", reg.Url ?? "", isConnected ? "connected" : "disconnected");
    }
}
