using GodMode.Shared.Models;
using SignalR.Proxy;

namespace GodMode.ClientBase.Services;

/// <summary>
/// The servers reachable through the registrations: lists them, resolves one for the relay,
/// and starts, stops or unregisters one by its server ID.
/// </summary>
public interface IServerDirectory
{
    Task<IReadOnlyList<ServerInfo>> ListAllServersAsync(CancellationToken ct = default);

    /// <summary>Where to relay a connection to the server, or null when no registration serves it or it is unreachable.</summary>
    Task<RelayTarget?> ResolveAsync(string serverId, CancellationToken ct = default);

    /// <summary>False when no registration serves the server.</summary>
    Task<bool> StartServerAsync(string serverId);

    /// <summary>False when no registration serves the server.</summary>
    Task<bool> StopServerAsync(string serverId);

    /// <summary>Removes the registration that serves the server (for a codespace, its GitHub account). False when none does.</summary>
    Task<bool> RemoveServerAsync(string serverId);
}
