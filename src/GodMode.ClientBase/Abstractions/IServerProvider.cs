using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.ClientBase.Abstractions;

/// <summary>
/// Provides access to server environments where projects can run.
/// ConnectAsync returns a raw HubConnection — consumers decide how to use it:
/// - MAUI proxy: raw InvokeCoreAsync for transparent forwarding
/// - Voice client: CreateHubProxy&lt;IProjectHub&gt;() for typed calls
/// </summary>
public interface IServerProvider
{
    string Type { get; }
    Task<IEnumerable<ServerInfo>> ListServersAsync();
    Task<ServerStatus> GetServerStatusAsync(string serverId);
    Task StartServerAsync(string serverId);
    Task StopServerAsync(string serverId);
    Task<HubConnection> ConnectAsync(string serverId);
}
