using Microsoft.AspNetCore.Authentication.Cookies;

namespace GodMode.Server.Auth;

/// <summary>
/// Validated Google auth configuration, loaded once at startup.
/// Only AllowedEmail is required — the OAuth proxy handles credentials.
/// </summary>
public record GoogleAuthOptions(string AllowedEmail);

public static class GoogleAuthExtensions
{
    /// <summary>
    /// Reads and validates Google auth config, registers cookie auth.
    /// The OAuth proxy handles the Google OAuth dance — no ClientId needed.
    /// </summary>
    public static IServiceCollection AddGoogleAuth(
        this IServiceCollection services, IConfiguration config)
    {
        var section = config.GetSection("Authentication:Google");
        var allowedEmail = section["AllowedEmail"]?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(allowedEmail) || !allowedEmail.Contains('@'))
            throw new InvalidOperationException(
                "Authentication:Google:AllowedEmail must be a valid email address");

        var options = new GoogleAuthOptions(allowedEmail);
        services.AddSingleton(options);

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(cookieOptions =>
            {
                cookieOptions.Cookie.Name = "GodMode.Auth";
                cookieOptions.Cookie.SameSite = SameSiteMode.Lax;
                cookieOptions.Cookie.HttpOnly = true;
                cookieOptions.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookieOptions.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = 401;
                    return Task.CompletedTask;
                };
            });
        services.AddAuthorization();
        return services;
    }
}
