using GodMode.ClientBase.Abstractions;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Manages HubConnections to servers via IServerProviders.
/// Returns raw HubConnection — consumers decide how to use it.
/// </summary>
public interface IServerConnectionService
{
    Task<IEnumerable<(IServerProvider Provider, int ServerIndex)>> GetAllProvidersAsync();
    Task<IEnumerable<ServerInfo>> ListAllServersAsync();
    HubConnection? GetConnection(string serverId);
    Task<HubConnection> ConnectToServerAsync(string serverId);
    Task DisconnectFromServerAsync(string serverId);
    Task DisconnectAllAsync();
    bool IsConnected(string serverId);
}
