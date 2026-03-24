namespace GodMode.Shared.Models;

/// <summary>
/// Request to add or update a GodMode server registration.
/// </summary>
public record AddServerRequest(
    string DisplayName,
    string Url,
    string? AccessToken = null
);
