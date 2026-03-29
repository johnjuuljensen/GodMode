using System.Security.Claims;
using GodMode.Server.Auth;
using GodMode.Server.Hubs;
using GodMode.Server.Services;
using GodMode.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) =>
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(".godmode-logs", "server-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 31));

// Detect auth mode
var isCodespace = string.Equals(
    Environment.GetEnvironmentVariable("CODESPACES"), "true", StringComparison.OrdinalIgnoreCase);
var apiKey = builder.Configuration["Authentication:ApiKey"];
var googleClientId = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID")
    ?? builder.Configuration["Authentication:Google:ClientId"];
var useGoogleAuth = !string.IsNullOrEmpty(googleClientId);
var requireAuth = isCodespace || !string.IsNullOrEmpty(apiKey) || useGoogleAuth;

// Add services to the container
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        var defaults = JsonDefaults.Options;
        options.PayloadSerializerOptions.PropertyNamingPolicy = defaults.PropertyNamingPolicy;
        options.PayloadSerializerOptions.DefaultIgnoreCondition = defaults.DefaultIgnoreCondition;
        foreach (var converter in defaults.Converters)
            options.PayloadSerializerOptions.Converters.Add(converter);
    });

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()
              .SetIsOriginAllowed(_ => true);
    });
});

builder.Services.AddSingleton<GoogleAuthConfig>();
builder.Services.AddHttpClient();

if (useGoogleAuth)
{
    builder.Services.AddSingleton(new GoogleTokenValidator(googleClientId!));
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "GodMode.Auth";
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.HttpOnly = true;
            options.Events.OnRedirectToLogin = context =>
            {
                // Return 401 for API/SignalR requests instead of redirect
                if (context.Request.Path.StartsWithSegments("/api") ||
                    context.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Response.StatusCode = 401;
                    return Task.CompletedTask;
                }
                context.Response.StatusCode = 401;
                return Task.CompletedTask;
            };
        });
    builder.Services.AddAuthorization();
}
else if (isCodespace || !string.IsNullOrEmpty(apiKey))
{
    builder.Services.AddAuthentication(GodModeAuthExtensions.SchemeName).AddGodModeAuth();
    builder.Services.AddAuthorization();
}

// Register application services
builder.Services.AddSingleton<IClaudeProcessManager, ClaudeProcessManager>();
builder.Services.AddSingleton<IStatusUpdater, StatusUpdater>();
builder.Services.AddSingleton<IRootConfigReader, RootConfigReader>();
builder.Services.AddSingleton<IScriptRunner, ScriptRunner>();
builder.Services.AddSingleton<IProjectManager, ProjectManager>();

var app = builder.Build();

// Configure the HTTP request pipeline
app.UseCors();

if (requireAuth)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

// Auth status endpoint — tells React what auth state we're in
app.MapGet("/api/auth/status", (GoogleAuthConfig googleAuth, HttpContext ctx) =>
{
    var isAuthenticated = ctx.User.Identity?.IsAuthenticated == true;
    var email = ctx.User.FindFirstValue(ClaimTypes.Email);

    return new
    {
        googleAuthEnabled = useGoogleAuth,
        googleClientId = useGoogleAuth ? googleClientId : null,
        configured = googleAuth.IsConfigured,
        allowedEmail = googleAuth.GetAllowedEmail(),
        authenticated = isAuthenticated,
        email
    };
}).AllowAnonymous();

// Configure allowed email (only when not yet configured)
app.MapPost("/api/auth/configure", (GoogleAuthConfig googleAuth, ConfigureEmailRequest request) =>
{
    if (googleAuth.IsConfigured)
        return Results.BadRequest(new { error = "Already configured" });

    if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
        return Results.BadRequest(new { error = "Invalid email address" });

    googleAuth.SetAllowedEmail(request.Email);
    return Results.Ok(new { success = true, email = googleAuth.GetAllowedEmail() });
}).AllowAnonymous();

// Google ID token login — client sends token from GIS, server validates and issues cookie
app.MapPost("/api/auth/google-login", async (
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
        return Results.Json(new { error = "unauthorized", message = $"Access denied. Only {allowedEmail} is allowed." }, statusCode: 403);

    // Issue cookie
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
app.MapPost("/api/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { success = true });
}).AllowAnonymous();

app.MapGet("/", () => new
{
    service = "GodMode.Server",
    version = "1.0.0",
    status = "running"
}).AllowAnonymous();

app.MapGet("/health", () => new { status = "healthy" }).AllowAnonymous();

var hub = app.MapHub<ProjectHub>("/hubs/projects");
if (requireAuth)
    hub.RequireAuthorization();

// Log auth mode
var authMode = useGoogleAuth ? "Google OAuth (client-side)" :
    isCodespace ? "Codespace (GitHub PAT)" :
    !string.IsNullOrEmpty(apiKey) ? "API Key" : "None (local)";
app.Logger.LogInformation("Authentication mode: {AuthMode}", authMode);

// Recover existing projects AFTER server starts (non-blocking)
var projectManager = app.Services.GetRequiredService<IProjectManager>();
app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(async () =>
    {
        try
        {
            await projectManager.RecoverProjectsAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error recovering projects: {ex.Message}");
        }
    });
});

app.Run();

record ConfigureEmailRequest(string Email);
record GoogleLoginRequest(string Credential);
