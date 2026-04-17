using System.Security.Claims;
using System.Text.RegularExpressions;

namespace GodMode.Vault.Services;

/// <summary>
/// Extracts a stable user identifier from the authenticated claims.
/// Google: "sub" claim from OIDC id_token.
/// GitHub: "id" claim (numeric user ID, stable across renames).
/// </summary>
public static partial class UserIdentity
{
    public static string GetUserSub(ClaimsPrincipal user)
    {
        // Google OIDC: NameIdentifier is the "sub" claim
        // GitHub OAuth: NameIdentifier is the user ID
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Authenticated user has no sub/id claim.");

        // Prefix with provider to avoid collisions between Google sub and GitHub user ID
        var provider = user.FindFirstValue("provider")
            ?? user.Identity?.AuthenticationType
            ?? "unknown";

        return $"{provider}:{sub}";
    }

    public static string? GetDisplayName(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.Name)
        ?? user.FindFirstValue(ClaimTypes.Email)
        ?? user.FindFirstValue("login"); // GitHub login name

    /// <summary>Replaces path-unsafe characters in a user sub (e.g. "google:123" → "google_123").</summary>
    public static string SanitizeSubForPath(string userSub) =>
        UnsafeSubRegex().Replace(userSub, "_");

    [GeneratedRegex(@"[^a-zA-Z0-9_-]")]
    private static partial Regex UnsafeSubRegex();
}
