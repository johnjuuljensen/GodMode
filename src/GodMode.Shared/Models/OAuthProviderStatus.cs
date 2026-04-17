namespace GodMode.Shared.Models;

/// <summary>
/// Status of an OAuth provider connection for a profile.
/// </summary>
public record OAuthProviderStatus(bool Connected, string? ExpiresAt = null, string? Email = null);
