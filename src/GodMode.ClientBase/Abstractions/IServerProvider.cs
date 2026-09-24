using GodMode.Shared.Models;
using SignalR.Proxy;

namespace GodMode.ClientBase.Abstractions;

/// <summary>
/// Provides access to server environments where projects can run.
/// </summary>
public interface IServerProvider
{
    string Type { get; }
    Task<IReadOnlyList<ServerInfo>> ListServersAsync(CancellationToken ct = default);

    /// <summary>Whether this provider serves the server with that ID.</summary>
    Task<bool> OwnsAsync(string serverId);

    Task StartServerAsync(string serverId);
    Task StopServerAsync(string serverId);

    /// <summary>Where to relay a connection to the server, or null when it is unknown or unreachable.</summary>
    Task<RelayTarget?> ResolveAsync(string serverId, CancellationToken ct = default);
}
