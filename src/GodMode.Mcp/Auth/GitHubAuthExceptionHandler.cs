using System.Security.Claims;
using GodMode.Mcp.OAuth;
using Octokit;

namespace GodMode.Mcp.Auth;

public class GitHubAuthExceptionHandler
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GitHubAuthExceptionHandler> _logger;

    public GitHubAuthExceptionHandler(RequestDelegate next, ILogger<GitHubAuthExceptionHandler> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IOAuthStore oauthStore)
    {
        try
        {
            await _next(context);
        }
        catch (AuthorizationException ex)
        {
            _logger.LogWarning(ex, "GitHub token was rejected - invalidating session");

            await InvalidateTokenAsync(context, oauthStore);

            context.Response.StatusCode = 401;
            context.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\", error_description=\"GitHub authorization expired or revoked\"";
        }
    }

    private async Task InvalidateTokenAsync(HttpContext context, IOAuthStore oauthStore)
    {
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader["Bearer ".Length..].Trim();
            await oauthStore.DeleteTokenAsync(token);
        }
    }
}

public static class GitHubAuthExceptionHandlerExtensions
{
    public static IApplicationBuilder UseGitHubAuthExceptionHandler(this IApplicationBuilder app)
    {
        return app.UseMiddleware<GitHubAuthExceptionHandler>();
    }
}
