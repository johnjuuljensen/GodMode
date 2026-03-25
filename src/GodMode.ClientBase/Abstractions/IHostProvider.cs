using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.ClientBase.Abstractions;

/// <summary>
/// Provides access to host environments where projects can run.
/// ConnectAsync returns a raw HubConnection — consumers decide how to use it:
/// - MAUI proxy: raw InvokeCoreAsync for transparent forwarding
/// - Voice client: CreateHubProxy&lt;IProjectHub&gt;() for typed calls
/// </summary>
public interface IHostProvider
{
    string Type { get; }
    Task<IEnumerable<HostInfo>> ListHostsAsync();
    Task<HostStatus> GetHostStatusAsync(string hostId);
    Task StartHostAsync(string hostId);
    Task StopHostAsync(string hostId);
    Task<HubConnection> ConnectAsync(string hostId);
}
