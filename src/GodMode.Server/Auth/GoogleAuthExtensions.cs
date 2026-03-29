using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace GodMode.Server.Auth;

public static class GoogleAuthExtensions
{
    /// <summary>
    /// Registers Google OAuth services: token validator, cookie authentication.
    /// </summary>
    public static IServiceCollection AddGoogleAuth(
        this IServiceCollection services, string clientId)
    {
        services.AddSingleton(sp => new GoogleTokenValidator(
            clientId, sp.GetRequiredService<ILogger<GoogleTokenValidator>>()));
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
    /// Maps the Google-specific login endpoint onto an existing route group.
    /// </summary>
    public static RouteGroupBuilder MapGoogleLoginEndpoint(this RouteGroupBuilder group)
    {
        group.MapPost("/google/login", async (
            GoogleTokenValidator validator,
            IConfiguration config,
            HttpContext ctx,
            GoogleLoginRequest request) =>
        {
            var payload = await validator.ValidateAsync(request.Credential);
            if (payload == null)
                return Results.Unauthorized();

            var email = payload.Email?.ToLowerInvariant();
            var allowedEmail = config["Authentication:Google:AllowedEmail"]?.Trim().ToLowerInvariant();

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

        return group;
    }
}

record GoogleLoginRequest(string Credential);
