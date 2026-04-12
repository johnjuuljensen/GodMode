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
    /// Fetch Google user info (email, name) using an access token.
    /// The proxy doesn't return user info, so we call Google's userinfo endpoint directly.
    /// </summary>
    /// <summary>
    /// Fetch Google user info (email, name) using an access token.
    /// Tries userinfo endpoint first, falls back to tokeninfo if scopes are insufficient.
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
                if (!string.IsNullOrEmpty(email))
                    return (email, name);
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
                _logger.LogInformation("Google tokeninfo returned email: {Email}", email);
                return (email, null);
            }
            var body = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning("Google tokeninfo failed ({Status}): {Body}", resp.StatusCode, body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch Google token info");
        }

        return (null, null);
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
