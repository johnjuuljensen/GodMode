using GodMode.Shared.Models;

namespace GodMode.ClientBase.Abstractions;

/// <summary>
/// Provides access to host environments where projects can run
/// </summary>
public interface IHostProvider
{
    /// <summary>
    /// Gets the type identifier for this provider (e.g., "github", "local")
    /// </summary>
    string Type { get; }

    /// <summary>
    /// Lists all available hosts from this provider
    /// </summary>
    Task<IEnumerable<HostInfo>> ListHostsAsync();

    /// <summary>
    /// Gets the current status of a specific host
    /// </summary>
    Task<HostStatus> GetHostStatusAsync(string hostId);

    /// <summary>
    /// Starts a host (e.g., starts a codespace)
    /// </summary>
    Task StartHostAsync(string hostId);

    /// <summary>
    /// Stops a host (e.g., stops a codespace)
    /// </summary>
    Task StopHostAsync(string hostId);

    /// <summary>
    /// Establishes a connection to a host for project operations
    /// </summary>
    Task<IProjectConnection> ConnectAsync(string hostId);
}
