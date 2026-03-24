namespace GodMode.Shared.Models;

/// <summary>
/// Represents a registered GodMode server as seen by the React client.
/// </summary>
public record ServerInfo(
    string Id,
    string DisplayName,
    string Url,
    string ConnectionState
);
