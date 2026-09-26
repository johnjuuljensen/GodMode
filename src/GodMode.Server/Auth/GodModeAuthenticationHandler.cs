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
/// Every mode needs a credential: there is no keyless access, from loopback or anywhere else.
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
        // A project's claude calling the MCP endpoint is not a user; its token must never be checked
        // as an API key or sent to GitHub. The project-token scheme handles it.
        if (Request.Headers.ContainsKey(ProjectTokenAuthenticationHandler.ProjectIdHeader))
            return AuthenticateResult.NoResult();

        // Authorization header; for the hub only, also the query string, because browsers cannot set
        // headers on a WebSocket upgrade. Nowhere else: a key in a URL ends up in logs and history.
        var queryToken = Request.Path.StartsWithSegments(GodModeAuthExtensions.HubPath)
            ? Request.Query["access_token"].FirstOrDefault()
            : null;
        if ((BearerToken.FromHeader(Request) ?? queryToken) is not { Length: > 0 } token)
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

        // The codespace hands its own token to its sessions (a root's environment passes GITHUB_TOKEN),
        // so a session, or a token it leaked, must not open the server with it. Checked before the
        // cache and GitHub, which would say it is GITHUB_USER's
        if (_settings.CodespaceToken is { Length: > 0 } codespaceToken && Secret.FixedTimeEquals(token, codespaceToken))
            return AuthenticateResult.Fail("The codespace's own GITHUB_TOKEN is not accepted: its sessions are given it");

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

    private AuthenticateResult ValidateApiKey(string token, string expectedKey) =>
        Secret.FixedTimeEquals(token, expectedKey)
            ? SuccessResult(Scheme.Name, "api-key-user")
            : AuthenticateResult.Fail("Invalid API key");

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
/// Authenticates a project's claude calling GodMode's MCP endpoint: <c>X-GodMode-Project-Id</c>
/// plus the bearer token that project's latest launch was issued, both from the MCP config the
/// server launched it with. A user's API key is not a project token, nor is another project's.
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

internal static class Secret
{
    /// <summary>
    /// Whether a presented secret is the expected one, in time that depends on neither's content or
    /// length: both are hashed, and the hashes compared in fixed time.
    /// </summary>
    public static bool FixedTimeEquals(string presented, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
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
    public const string HubPath = "/hubs/projects";
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
