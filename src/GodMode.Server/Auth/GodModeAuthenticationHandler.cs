using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GodMode.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace GodMode.Server.Auth;

/// <summary>
/// Authenticates users of the server (React client, MAUI relay) according to the startup <see cref="AuthMode"/>.
/// </summary>
public class GodModeAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private static readonly ConcurrentDictionary<string, (string user, DateTime expiry)> TokenCache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AuthSettings _settings;

    public GodModeAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IHttpClientFactory httpClientFactory,
        AuthSettings settings)
        : base(options, logger, encoder)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // A project's MCP bridge is not a user; its token must never be checked as an API key
        // or sent to GitHub. The project-token scheme handles it.
        if (Request.Headers.ContainsKey(ProjectTokenAuthenticationHandler.ProjectIdHeader))
            return AuthenticateResult.NoResult();

        // Loopback mode is only selected when every binding is loopback; checking the caller
        // as well keeps it closed if the server is reachable some other way.
        if (_settings.Mode == AuthMode.Loopback)
            return AuthModeSelector.IsLoopback(Context.Connection.RemoteIpAddress)
                ? SuccessResult(Scheme.Name, "local-user")
                : AuthenticateResult.Fail("Unauthenticated access is only allowed from loopback");

        // Authorization header, or query string (SignalR sends the token there for WebSocket upgrade)
        if ((BearerToken.FromHeader(Request) ?? Request.Query["access_token"].FirstOrDefault()) is not { Length: > 0 } token)
            return AuthenticateResult.NoResult();

        return _settings.Mode switch
        {
            AuthMode.Codespace => await ValidateGitHubTokenAsync(token),
            AuthMode.ApiKey => ValidateApiKey(token, _settings.ApiKey!),
            _ => AuthenticateResult.NoResult(),
        };
    }

    private async Task<AuthenticateResult> ValidateGitHubTokenAsync(string token)
    {
        var expectedUser = _settings.GitHubUser;
        if (string.IsNullOrEmpty(expectedUser))
            return AuthenticateResult.Fail("GITHUB_USER environment variable not set");

        var cacheKey = HashToken(token);

        // Check cache
        if (TokenCache.TryGetValue(cacheKey, out var cached) && cached.expiry > DateTime.UtcNow)
        {
            if (string.Equals(cached.user, expectedUser, StringComparison.OrdinalIgnoreCase))
                return SuccessResult(Scheme.Name, cached.user);

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
                return SuccessResult(Scheme.Name, login);

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

        return SuccessResult(Scheme.Name, "api-key-user");
    }

    internal static AuthenticateResult SuccessResult(string scheme, string username, params Claim[] extraClaims)
    {
        Claim[] claims =
        [
            new Claim(ClaimTypes.NameIdentifier, username),
            new Claim(ClaimTypes.Name, username),
            .. extraClaims,
        ];

        var identity = new ClaimsIdentity(claims, scheme);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, scheme));
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

/// <summary>
/// Authenticates the GodMode MCP bridge running inside a project: <c>X-GodMode-Project-Id</c>
/// plus that project's bearer token. A user's API key is not a project token.
/// </summary>
public class ProjectTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IProjectManager projectManager)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string ProjectIdHeader = "X-GodMode-Project-Id";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var projectId = Request.Headers[ProjectIdHeader].ToString();
        if (string.IsNullOrEmpty(projectId) || BearerToken.FromHeader(Request) is not { Length: > 0 } token)
            return Task.FromResult(AuthenticateResult.NoResult());

        return Task.FromResult(projectManager.ValidateProjectToken(projectId, token) == null
            ? AuthenticateResult.Fail("Invalid project token")
            : GodModeAuthenticationHandler.SuccessResult(Scheme.Name, $"project:{projectId}",
                new Claim(GodModeAuthExtensions.ProjectIdClaim, projectId)));
    }
}

internal static class BearerToken
{
    public static string? FromHeader(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }
}

public static class GodModeAuthExtensions
{
    public const string SchemeName = "GodModeBearer";
    public const string ProjectTokenSchemeName = "GodModeProjectToken";
    public const string ProjectPolicy = "GodModeProject";
    public const string ProjectIdClaim = "godmode:project-id";

    /// <summary>
    /// Registers user and project authentication, and makes authentication the default:
    /// every endpoint requires an authenticated user unless it explicitly opts out (only /health and the SPA do).
    /// </summary>
    public static IServiceCollection AddGodModeAuth(this IServiceCollection services, AuthSettings settings)
    {
        services.AddSingleton(settings);
        services.AddAuthentication(SchemeName)
            .AddScheme<AuthenticationSchemeOptions, GodModeAuthenticationHandler>(SchemeName, _ => { })
            .AddScheme<AuthenticationSchemeOptions, ProjectTokenAuthenticationHandler>(ProjectTokenSchemeName, _ => { });
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder(SchemeName).RequireAuthenticatedUser().Build())
            .AddPolicy(ProjectPolicy, policy => policy
                .AddAuthenticationSchemes(ProjectTokenSchemeName)
                .RequireAuthenticatedUser()
                .RequireClaim(ProjectIdClaim));
        return services;
    }
}
