using GodMode.ClientBase.Abstractions;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Manages HubConnections to hosts via IServerProviders.
/// Returns raw HubConnection — consumers decide how to use it.
/// </summary>
public interface IServerConnectionService
{
    Task<IEnumerable<(IServerProvider Provider, int ServerIndex)>> GetAllProvidersAsync();
    Task<IEnumerable<ServerInfo>> ListAllHostsAsync();
    HubConnection? GetConnection(string hostId);
    Task<HubConnection> ConnectToHostAsync(string hostId);
    Task DisconnectFromHost(string hostId);
    Task DisconnectAllAsync();
    bool IsConnected(string hostId);
}
