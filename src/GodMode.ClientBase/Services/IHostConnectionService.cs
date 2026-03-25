using GodMode.ClientBase.Abstractions;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Manages HubConnections to hosts.
/// Returns raw HubConnection — consumers decide how to use it.
/// </summary>
public interface IHostConnectionService
{
    Task<IEnumerable<(IHostProvider Provider, int ServerIndex)>> GetAllProvidersAsync();
    Task<IEnumerable<HostInfo>> ListAllHostsAsync();
    Task<HubConnection> ConnectToHostAsync(string hostId);
    Task DisconnectFromHost(string hostId);
    Task DisconnectAll();
    bool IsConnected(string hostId);
}
