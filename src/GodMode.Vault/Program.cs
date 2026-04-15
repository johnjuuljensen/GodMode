using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using GodMode.Vault.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSingleton<EncryptionService>();
builder.Services.AddSingleton<FileSecretStore>();

// --- Authentication: Cookie + Google + GitHub ---

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "GodMode.Vault";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;

        // API-friendly: return 401 instead of redirect for API calls
        options.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                ctx.Response.StatusCode = 401;
                return Task.CompletedTask;
            }
            ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
    })
    .AddGoogle(options =>
    {
        options.ClientId = builder.Configuration["Auth:Google:ClientId"] ?? "";
        options.ClientSecret = builder.Configuration["Auth:Google:ClientSecret"] ?? "";
        options.CallbackPath = "/auth/callback/google";
        options.SaveTokens = true;

        options.Events.OnCreatingTicket = ctx =>
        {
            ctx.Identity?.AddClaim(new Claim("provider", "google"));
            return Task.CompletedTask;
        };
    })
    .AddOAuth("GitHub", options =>
    {
        options.ClientId = builder.Configuration["Auth:GitHub:ClientId"] ?? "";
        options.ClientSecret = builder.Configuration["Auth:GitHub:ClientSecret"] ?? "";
        options.CallbackPath = "/auth/callback/github";
        options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
        options.TokenEndpoint = "https://github.com/login/oauth/access_token";
        options.UserInformationEndpoint = "https://api.github.com/user";
        options.SaveTokens = true;

        options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
        options.ClaimActions.MapJsonKey(ClaimTypes.Name, "login");
        options.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");
        options.ClaimActions.MapJsonKey("login", "login");

        options.Events = new OAuthEvents
        {
            OnCreatingTicket = async ctx =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ctx.Options.UserInformationEndpoint);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ctx.AccessToken);
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                using var response = await ctx.Backchannel.SendAsync(request, ctx.HttpContext.RequestAborted);
                response.EnsureSuccessStatusCode();

                var user = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                ctx.RunClaimActions(user.RootElement);
                ctx.Identity?.AddClaim(new Claim("provider", "github"));
            }
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

// --- Auth endpoints ---

app.MapGet("/auth/login/google", (HttpContext ctx) =>
    Results.Challenge(
        new AuthenticationProperties { RedirectUri = "/" },
        ["Google"]));

app.MapGet("/auth/login/github", (HttpContext ctx) =>
    Results.Challenge(
        new AuthenticationProperties { RedirectUri = "/" },
        ["GitHub"]));

app.MapGet("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
});

app.MapGet("/auth/me", (HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Json(new { authenticated = false });

    return Results.Json(new
    {
        authenticated = true,
        provider = ctx.User.FindFirstValue("provider"),
        name = UserIdentity.GetDisplayName(ctx.User),
        sub = UserIdentity.GetUserSub(ctx.User)
    });
});

// --- Health ---

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "GodMode.Vault" }));

// --- Generate a master secret (dev utility) ---

app.MapGet("/admin/generate-secret", () =>
    Results.Text(EncryptionService.GenerateMasterSecret()))
    .RequireAuthorization();

app.MapControllers();
app.Run();
