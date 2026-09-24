using GodMode.ClientBase.Services.Models;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Manages server registrations. Registrations are kept in servers.json;
/// their access tokens are kept in secure storage and never written to a file.
/// </summary>
public interface IServerRegistryService
{
    /// <summary>Gets all registered servers.</summary>
    Task<IReadOnlyList<ServerRegistration>> GetServersAsync();

    /// <summary>
    /// Registers a server under a new unique ID (any <see cref="ServerRegistration.Id"/> passed in is ignored)
    /// and puts its access token, if any, in secure storage. When secure storage refuses the token, the server
    /// is not added and this throws an <see cref="InvalidOperationException"/> whose message says so.
    /// </summary>
    Task<ServerRegistration> AddServerAsync(ServerRegistration server, string? accessToken);

    /// <summary>Removes a registration and its stored token. False when no registration has that ID.</summary>
    Task<bool> RemoveServerAsync(string id);

    /// <summary>The access token stored for a registration, or null when it has none. Throws when secure storage cannot be read.</summary>
    Task<string?> GetAccessTokenAsync(string id);
}
