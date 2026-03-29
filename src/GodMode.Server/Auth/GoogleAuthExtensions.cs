using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace GodMode.Server.Auth;

public static class GoogleAuthExtensions
{
    /// <summary>
    /// Registers Google OAuth services: config, token validator, cookie authentication.
    /// </summary>
    public static IServiceCollection AddGoogleAuth(
        this IServiceCollection services, string clientId)
    {
        services.AddSingleton(new GoogleTokenValidator(clientId));
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "GodMode.Auth";
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.HttpOnly = true;
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = 401;
                    return Task.CompletedTask;
                };
            });
        services.AddAuthorization();
        return services;
    }

    /// <summary>
    /// Maps Google OAuth endpoints under /api/auth.
    /// </summary>
    public static WebApplication MapGoogleAuthEndpoints(
        this WebApplication app, string clientId)
    {
        var group = app.MapGroup("/api/auth");

        // Auth challenge — tells the client what auth method is required.
        // Only exposes the method type and public client ID (no sensitive config).
        group.MapGet("/challenge", (HttpContext ctx) =>
        {
            var isAuthenticated = ctx.User.Identity?.IsAuthenticated == true;
            return Results.Ok(new
            {
                method = "google",
                clientId,
                authenticated = isAuthenticated,
            });
        }).AllowAnonymous();

        // Google ID token login — client sends token from GIS, server validates and issues cookie
        group.MapPost("/google-login", async (
            GoogleTokenValidator validator,
            GoogleAuthConfig googleAuth,
            HttpContext ctx,
            GoogleLoginRequest request) =>
        {
            var payload = await validator.ValidateAsync(request.Credential);
            if (payload == null)
                return Results.Unauthorized();

            var email = payload.Email?.ToLowerInvariant();
            var allowedEmail = googleAuth.GetAllowedEmail();

            if (string.IsNullOrEmpty(email) || email != allowedEmail)
                return Results.Json(new { error = "access_denied" }, statusCode: 403);

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, payload.Subject),
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.Name, payload.Name ?? email),
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

            return Results.Ok(new { success = true, email });
        }).AllowAnonymous();

        // Logout
        group.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { success = true });
        }).AllowAnonymous();

        return app;
    }
}

record GoogleLoginRequest(string Credential);
