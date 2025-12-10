using System.Security.Claims;
using System.Text.Encodings.Web;
using GodMode.Mcp.OAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace GodMode.Mcp.Auth;

public class McpAuthenticationOptions : AuthenticationSchemeOptions
{
}

public class McpAuthenticationHandler : AuthenticationHandler<McpAuthenticationOptions>
{
    private readonly IOAuthStore _oauthStore;

    public McpAuthenticationHandler(
        IOptionsMonitor<McpAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOAuthStore oauthStore)
        : base(options, logger, encoder)
    {
        _oauthStore = oauthStore;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authHeader = Request.Headers.Authorization.ToString();

        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = authHeader["Bearer ".Length..].Trim();

        var tokenRecord = await _oauthStore.GetTokenAsync(token);
        if (tokenRecord == null)
        {
            return AuthenticateResult.Fail("Invalid or expired token");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, tokenRecord.GitHubUserId),
            new Claim("github_access_token", tokenRecord.GitHubAccessToken),
            new Claim("client_id", tokenRecord.ClientId)
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
}

public static class GodModeMcpAuthExtensions
{
    public const string SchemeName = "McpBearer";

    public static AuthenticationBuilder AddGodModeMcpAuth(
        this AuthenticationBuilder builder,
        Action<McpAuthenticationOptions>? configure = null)
    {
        return builder.AddScheme<McpAuthenticationOptions, McpAuthenticationHandler>(
            SchemeName,
            configure ?? (_ => { }));
    }
}
