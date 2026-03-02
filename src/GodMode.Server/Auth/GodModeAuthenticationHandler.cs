using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace GodMode.Server.Auth;

public class GodModeAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private static readonly ConcurrentDictionary<string, (string user, DateTime expiry)> TokenCache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public GodModeAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Extract token from Authorization header or query string (SignalR WebSocket upgrade)
        var token = ExtractToken();
        if (token == null)
            return AuthenticateResult.NoResult();

        var isCodespace = string.Equals(
            Environment.GetEnvironmentVariable("CODESPACES"), "true", StringComparison.OrdinalIgnoreCase);

        if (isCodespace)
            return await ValidateGitHubTokenAsync(token);

        var apiKey = _configuration["Authentication:ApiKey"];
        if (!string.IsNullOrEmpty(apiKey))
            return ValidateApiKey(token, apiKey);

        // No auth mode configured — should not reach here if middleware is wired correctly
        return AuthenticateResult.NoResult();
    }

    private string? ExtractToken()
    {
        // Check Authorization header first
        var authHeader = Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authHeader["Bearer ".Length..].Trim();

        // Fall back to query string (SignalR sends token here for WebSocket upgrade)
        var queryToken = Request.Query["access_token"].FirstOrDefault();
        if (!string.IsNullOrEmpty(queryToken))
            return queryToken;

        return null;
    }

    private async Task<AuthenticateResult> ValidateGitHubTokenAsync(string token)
    {
        var expectedUser = Environment.GetEnvironmentVariable("GITHUB_USER");
        if (string.IsNullOrEmpty(expectedUser))
            return AuthenticateResult.Fail("GITHUB_USER environment variable not set");

        var cacheKey = HashToken(token);

        // Check cache
        if (TokenCache.TryGetValue(cacheKey, out var cached) && cached.expiry > DateTime.UtcNow)
        {
            if (string.Equals(cached.user, expectedUser, StringComparison.OrdinalIgnoreCase))
                return SuccessResult(cached.user);

            return AuthenticateResult.Fail("Token owner does not match GITHUB_USER");
        }

        // Validate against GitHub API
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("GodMode", "1.0"));

            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return AuthenticateResult.Fail("GitHub token validation failed");

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var login = json.GetProperty("login").GetString();

            if (string.IsNullOrEmpty(login))
                return AuthenticateResult.Fail("GitHub API returned no login");

            // Cache the result
            TokenCache[cacheKey] = (login, DateTime.UtcNow + CacheTtl);

            if (string.Equals(login, expectedUser, StringComparison.OrdinalIgnoreCase))
                return SuccessResult(login);

            return AuthenticateResult.Fail("Token owner does not match GITHUB_USER");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "GitHub token validation error");
            return AuthenticateResult.Fail("GitHub token validation error");
        }
    }

    private AuthenticateResult ValidateApiKey(string token, string expectedKey)
    {
        // Constant-time comparison
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        var keyBytes = Encoding.UTF8.GetBytes(expectedKey);

        if (!CryptographicOperations.FixedTimeEquals(tokenBytes, keyBytes))
            return AuthenticateResult.Fail("Invalid API key");

        return SuccessResult("api-key-user");
    }

    private AuthenticateResult SuccessResult(string username)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, username),
            new Claim(ClaimTypes.Name, username)
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    private static string HashToken(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(hash);
    }
}

public static class GodModeAuthExtensions
{
    public const string SchemeName = "GodModeBearer";

    public static AuthenticationBuilder AddGodModeAuth(
        this AuthenticationBuilder builder,
        Action<AuthenticationSchemeOptions>? configure = null)
    {
        return builder.AddScheme<AuthenticationSchemeOptions, GodModeAuthenticationHandler>(
            SchemeName,
            configure ?? (_ => { }));
    }
}
