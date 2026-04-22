namespace GodMode.Shared.Models;

/// <summary>
/// OAuth tokens returned by the auth proxy after relay redemption or token refresh.
/// </summary>
public record OAuthTokenSet(
    string AccessToken,
    string? RefreshToken,
    long ExpiresAt,
    string Provider,
    string? Email = null,
    string? Name = null,
    bool? EmailVerified = null)
{
    public bool IsExpired => DateTimeOffset.FromUnixTimeSeconds(ExpiresAt) < DateTimeOffset.UtcNow;
}
