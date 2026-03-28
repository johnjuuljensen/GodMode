using GodMode.ClientBase.Abstractions;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Manages HubConnections to hosts via IHostProviders.
/// Returns raw HubConnection — consumers decide how to use it.
/// </summary>
public interface IServerConnectionService
{
    Task<IEnumerable<(IHostProvider Provider, int ServerIndex)>> GetAllProvidersAsync();
    Task<IEnumerable<HostInfo>> ListAllHostsAsync();
    HubConnection? GetConnection(string hostId);
    Task<HubConnection> ConnectToHostAsync(string hostId);
    Task DisconnectFromHost(string hostId);
    Task DisconnectAllAsync();
    bool IsConnected(string hostId);
}
