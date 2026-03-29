using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace GodMode.Server.Auth;

/// <summary>
/// Validated Google auth configuration, loaded once at startup.
/// </summary>
public record GoogleAuthOptions(string ClientId, string? AllowedEmail);

public static class GoogleAuthExtensions
{
    /// <summary>
    /// Reads and validates Google auth config, registers services.
    /// Throws on missing or malformed config — fail fast at startup.
    /// </summary>
    public static IServiceCollection AddGoogleAuth(
        this IServiceCollection services, IConfiguration config)
    {
        var section = config.GetSection("Authentication:Google");
        var clientId = section["ClientId"];
        var allowedEmail = section["AllowedEmail"]?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(clientId))
            throw new InvalidOperationException(
                "Authentication:Google:ClientId is required when Google auth is enabled");

        if (string.IsNullOrEmpty(allowedEmail) || !allowedEmail.Contains('@'))
            throw new InvalidOperationException(
                "Authentication:Google:AllowedEmail must be a valid email address");

        var options = new GoogleAuthOptions(clientId, allowedEmail);
        services.AddSingleton(options);

        services.AddSingleton(sp => new GoogleTokenValidator(
            options.ClientId, sp.GetRequiredService<ILogger<GoogleTokenValidator>>()));
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(cookieOptions =>
            {
                cookieOptions.Cookie.Name = "GodMode.Auth";
                cookieOptions.Cookie.SameSite = SameSiteMode.Lax;
                cookieOptions.Cookie.HttpOnly = true;
                cookieOptions.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = 401;
                    return Task.CompletedTask;
                };
            });
        services.AddAuthorization();
        return services;
    }

    /// <summary>
    /// Maps the Google-specific login endpoint onto an existing route group.
    /// </summary>
    public static RouteGroupBuilder MapGoogleLoginEndpoint(this RouteGroupBuilder group)
    {
        group.MapPost("/google/login", async (
            GoogleTokenValidator validator,
            GoogleAuthOptions options,
            HttpContext ctx,
            GoogleLoginRequest request) =>
        {
            var payload = await validator.ValidateAsync(request.Credential);
            if (payload == null)
                return Results.Unauthorized();

            var email = payload.Email?.ToLowerInvariant();

            if (string.IsNullOrEmpty(email) || email != options.AllowedEmail)
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

        return group;
    }
}

record GoogleLoginRequest(string Credential);
