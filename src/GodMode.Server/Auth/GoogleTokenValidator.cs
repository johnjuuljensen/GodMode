using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;

namespace GodMode.Server.Auth;

/// <summary>
/// Validates Google ID tokens (JWTs) using Google's public JWKS keys.
/// No redirect URIs needed — works with client-side Google Identity Services.
/// </summary>
public class GoogleTokenValidator
{
    private readonly string _clientId;
    private readonly ILogger<GoogleTokenValidator> _logger;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static JsonWebKeySet? _cachedKeys;
    private static DateTime _cacheExpiry;

    public GoogleTokenValidator(string clientId, ILogger<GoogleTokenValidator> logger)
    {
        _clientId = clientId;
        _logger = logger;
    }

    public async Task<GoogleTokenPayload?> ValidateAsync(string idToken)
    {
        try
        {
            var keys = await GetGoogleKeysAsync();
            var handler = new JwtSecurityTokenHandler
            {
                MapInboundClaims = false // Keep original claim names (email, sub, name) instead of mapping to long URIs
            };

            var validationParams = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuers = ["accounts.google.com", "https://accounts.google.com"],
                ValidateAudience = true,
                ValidAudience = _clientId,
                ValidateLifetime = true,
                IssuerSigningKeys = keys.GetSigningKeys(),
            };

            var principal = handler.ValidateToken(idToken, validationParams, out _);

            var email = principal.FindFirst("email")?.Value;
            var emailVerified = principal.FindFirst("email_verified")?.Value;
            var sub = principal.FindFirst("sub")?.Value;
            var name = principal.FindFirst("name")?.Value;

            if (emailVerified != "true")
                return null;

            return new GoogleTokenPayload
            {
                Subject = sub ?? "",
                Email = email,
                Name = name,
                EmailVerified = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google token validation failed");
            return null;
        }
    }

    private static async Task<JsonWebKeySet> GetGoogleKeysAsync()
    {
        if (_cachedKeys != null && DateTime.UtcNow < _cacheExpiry)
            return _cachedKeys;

        var json = await Http.GetStringAsync("https://www.googleapis.com/oauth2/v3/certs");
        _cachedKeys = new JsonWebKeySet(json);
        _cacheExpiry = DateTime.UtcNow.AddHours(6);
        return _cachedKeys;
    }
}

public class GoogleTokenPayload
{
    public string Subject { get; init; } = "";
    public string? Email { get; init; }
    public string? Name { get; init; }
    public bool EmailVerified { get; init; }
}
