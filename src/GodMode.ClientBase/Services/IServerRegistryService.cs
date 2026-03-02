using GodMode.ClientBase.Services.Models;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Manages server registrations (replacing profile-scoped accounts).
/// Servers are global — profiles are now discovered from servers, not defined on the client.
/// </summary>
public interface IServerRegistryService
{
    /// <summary>
    /// Gets all registered servers.
    /// </summary>
    Task<List<ServerRegistration>> GetServersAsync();

    /// <summary>
    /// Adds a new server registration.
    /// </summary>
    Task AddServerAsync(ServerRegistration server);

    /// <summary>
    /// Updates a server registration at the given index.
    /// </summary>
    Task UpdateServerAsync(int index, ServerRegistration server);

    /// <summary>
    /// Removes a server registration at the given index.
    /// </summary>
    Task RemoveServerAsync(int index);

    /// <summary>
    /// Checks if a duplicate server already exists.
    /// </summary>
    bool IsDuplicate(ServerRegistration server, int? excludeIndex = null);

    /// <summary>
    /// Decrypts a token that was encrypted for storage.
    /// </summary>
    string DecryptToken(string encryptedToken);

    /// <summary>
    /// Encrypts a token for storage.
    /// </summary>
    string EncryptToken(string token);
}
