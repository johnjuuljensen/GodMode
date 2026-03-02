using GodMode.ClientBase.Abstractions;
using GodMode.Shared.Models;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Interface for host connection management service.
/// Servers are global (not profile-scoped) — profiles are discovered from servers.
/// </summary>
public interface IHostConnectionService
{
    /// <summary>
    /// Gets all providers from registered servers.
    /// </summary>
    Task<IEnumerable<(IHostProvider Provider, int ServerIndex)>> GetAllProvidersAsync();

    /// <summary>
    /// Lists all hosts from all registered servers.
    /// </summary>
    Task<IEnumerable<HostInfo>> ListAllHostsAsync();

    /// <summary>
    /// Connects to a specific host by its ID.
    /// </summary>
    Task<IProjectConnection> ConnectToHostAsync(string hostId);

    /// <summary>
    /// Disconnects from a specific host.
    /// </summary>
    void DisconnectFromHost(string hostId);

    /// <summary>
    /// Disconnects from all hosts.
    /// </summary>
    void DisconnectAll();

    /// <summary>
    /// Checks if a specific host is connected.
    /// </summary>
    bool IsConnected(string hostId);

    // Backward-compat overloads (profileName is ignored, kept for transition)

    /// <inheritdoc cref="GetAllProvidersAsync"/>
    Task<IEnumerable<(IHostProvider Provider, int ServerIndex)>> GetProvidersForProfileAsync(string profileName);

    /// <inheritdoc cref="ListAllHostsAsync"/>
    Task<IEnumerable<HostInfo>> ListAllHostsAsync(string profileName);

    /// <inheritdoc cref="ConnectToHostAsync(string)"/>
    Task<IProjectConnection> ConnectToHostAsync(string profileName, string hostId);

    /// <inheritdoc cref="DisconnectFromHost(string)"/>
    void DisconnectFromHost(string profileName, string hostId);

    /// <inheritdoc cref="IsConnected(string)"/>
    bool IsConnected(string profileName, string hostId);
}
