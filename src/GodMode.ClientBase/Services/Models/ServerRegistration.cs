namespace GodMode.ClientBase.Services.Models;

/// <summary>Server registration types.</summary>
public static class ServerTypes
{
    /// <summary>A GodMode.Server reached at one or more URLs.</summary>
    public const string Local = "local";

    /// <summary>A GitHub account whose codespaces run GodMode.Server.</summary>
    public const string GitHub = "github";
}

/// <summary>
/// A registered server connection, stored in servers.json.
/// The access token is never part of it: it lives in the platform's secure storage, keyed by <see cref="Id"/>.
/// </summary>
public sealed record ServerRegistration
{
    /// <summary>Unique ID (a GUID) assigned when the server is registered.</summary>
    public string Id { get; init; } = "";

    /// <summary>Server type: <see cref="ServerTypes.Local"/> or <see cref="ServerTypes.GitHub"/>.</summary>
    public string Type { get; init; } = ServerTypes.Local;

    /// <summary>
    /// URLs of a local server, in order of preference. The relay connects to the first that answers /health.
    /// All of them share the server's access token.
    /// </summary>
    public IReadOnlyList<string> Urls { get; init; } = [];

    /// <summary>Username for GitHub Codespaces accounts.</summary>
    public string? Username { get; init; }

    /// <summary>Optional display name for the server.</summary>
    public string? DisplayName { get; init; }
}

/// <summary>
/// Container for server registrations stored in servers.json.
/// </summary>
public sealed record ServersConfig
{
    public IReadOnlyList<ServerRegistration> Servers { get; init; } = [];
}
