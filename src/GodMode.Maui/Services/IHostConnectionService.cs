using GodMode.Maui.Abstractions;
using GodMode.Shared.Models;

namespace GodMode.Maui.Services;

/// <summary>
/// Interface for host connection management service
/// </summary>
public interface IHostConnectionService
{
    Task<IEnumerable<IHostProvider>> GetProvidersForProfileAsync(string profileName);
    Task<IEnumerable<HostInfo>> ListAllHostsAsync(string profileName);
    Task<IProjectConnection> ConnectToHostAsync(string profileName, string hostId);
    void DisconnectFromHost(string profileName, string hostId);
    void DisconnectAll();
    bool IsConnected(string profileName, string hostId);
}
