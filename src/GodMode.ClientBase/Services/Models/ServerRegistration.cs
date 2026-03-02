namespace GodMode.ClientBase.Services.Models;

/// <summary>
/// A registered server connection.
/// Replaces the old per-profile Account model — servers are now global, not profile-scoped.
/// </summary>
public class ServerRegistration
{
    /// <summary>
    /// Server type: "local" or "github".
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Server URL for local servers (e.g., "http://localhost:31337").
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Username for GitHub Codespaces accounts.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Encrypted token for GitHub accounts.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// Optional display name for the server.
    /// </summary>
    public string? DisplayName { get; set; }
}

/// <summary>
/// Container for server registrations stored in servers.json.
/// </summary>
public class ServersConfig
{
    public List<ServerRegistration> Servers { get; set; } = new();
}
