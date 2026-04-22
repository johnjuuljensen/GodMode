using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// HTTP client for communicating with the GodMode OAuth proxy (auth.inGodMode.xyz).
/// </summary>
public class OAuthProxyClient
{
    private readonly HttpClient _http;
    private readonly ILogger<OAuthProxyClient> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const string DefaultProxyUrl = "https://auth.ingodmode.xyz";

    public OAuthProxyClient(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<OAuthProxyClient> logger)
    {
        _http = httpClientFactory.CreateClient("OAuthProxy");
        _http.BaseAddress = new Uri(config["OAuthProxy:Url"] ?? DefaultProxyUrl);
        _http.Timeout = TimeSpan.FromSeconds(15);
        _logger = logger;
    }

    /// <summary>
    /// Exchange a one-time relay token for OAuth tokens.
    /// </summary>
    public async Task<OAuthTokenSet?> RedeemRelayTokenAsync(string relayToken)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("/relay/redeem",
                new { relayToken }, JsonOptions);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Relay redeem failed: {Status}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<OAuthTokenSet>(JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to redeem relay token");
            return null;
        }
    }

    /// <summary>
    /// Refresh an expired access token.
    /// </summary>
    public async Task<OAuthTokenSet?> RefreshTokenAsync(string provider, string refreshToken)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("/token/refresh",
                new { provider, refreshToken }, JsonOptions);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Token refresh failed for {Provider}: {Status}", provider, response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<OAuthTokenSet>(JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh token for {Provider}", provider);
            return null;
        }
    }

    /// <summary>
    /// Fetch Google user info (email, name) using an access token, enforcing that Google has
    /// verified the email. Returns (null, null) when the email claim is missing or unverified
    /// (<c>verified_email</c> / <c>email_verified</c> is not <c>true</c>) — treating an
    /// unverified claim as trusted would allow an attacker controlling a federated Workspace
    /// tenant to assert the victim's email. Tries userinfo first, falls back to tokeninfo.
    /// </summary>
    public async Task<(string? Email, string? Name)> FetchGoogleUserInfoAsync(string accessToken)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // Try userinfo endpoint (requires openid/email scopes)
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v2/userinfo");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var resp = await httpClient.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
                var email = json.TryGetProperty("email", out var e) ? e.GetString() : null;
                var name = json.TryGetProperty("name", out var n) ? n.GetString() : null;
                var verified = TryGetBool(json, "verified_email") ?? TryGetBool(json, "email_verified");
                if (!string.IsNullOrEmpty(email))
                {
                    if (verified == true)
                        return (email, name);
                    _logger.LogWarning("Google userinfo email {Email} is not verified (verified_email={Verified}); rejecting", email, verified);
                    return (null, null);
                }
            }
            _logger.LogDebug("Google userinfo returned no email, trying tokeninfo");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Google userinfo failed, trying tokeninfo");
        }

        // Fallback: tokeninfo (works with any valid access token)
        try
        {
            var tokenInfoUrl = $"https://www.googleapis.com/oauth2/v3/tokeninfo?access_token={Uri.EscapeDataString(accessToken)}";
            var resp = await httpClient.GetAsync(tokenInfoUrl);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
                var email = json.TryGetProperty("email", out var e) ? e.GetString() : null;
                var verified = TryGetBool(json, "email_verified") ?? TryGetBool(json, "verified_email");
                _logger.LogDebug("Google tokeninfo returned email: {Email} verified={Verified}", email, verified);
                if (!string.IsNullOrEmpty(email))
                {
                    if (verified == true)
                        return (email, null);
                    _logger.LogWarning("Google tokeninfo email {Email} is not verified (email_verified={Verified}); rejecting", email, verified);
                    return (null, null);
                }
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync();
                _logger.LogWarning("Google tokeninfo failed ({Status}): {Body}", resp.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch Google token info");
        }

        return (null, null);
    }

    // Google returns email_verified/verified_email as either a JSON boolean or the string "true"/"false".
    internal static bool? TryGetBool(JsonElement json, string propertyName)
    {
        if (!json.TryGetProperty(propertyName, out var prop))
            return null;
        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(prop.GetString(), out var b) => b,
            _ => null,
        };
    }

    /// <summary>
    /// Build the authorization URL for the proxy.
    /// </summary>
    public string BuildAuthorizeUrl(string provider, string instanceUrl, string state, string? profileId, string? scope = null)
    {
        var url = $"{_http.BaseAddress}authorize?provider={Uri.EscapeDataString(provider)}" +
                  $"&instance={Uri.EscapeDataString(instanceUrl)}" +
                  $"&state={Uri.EscapeDataString(state)}";

        if (!string.IsNullOrEmpty(profileId))
            url += $"&profile={Uri.EscapeDataString(profileId)}";

        if (!string.IsNullOrEmpty(scope))
            url += $"&scope={Uri.EscapeDataString(scope)}";

        return url;
    }
}
