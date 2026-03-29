using GodMode.Server.Auth;
using GodMode.Server.Hubs;
using GodMode.Server.Services;
using GodMode.Shared;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
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
    builder.Services.AddGoogleAuth(googleClientId!);
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

// Serve the React client from wwwroot/ (if present)
app.UseDefaultFiles();
app.UseStaticFiles();

if (useGoogleAuth)
    app.MapGoogleAuthEndpoints(googleClientId!);

app.MapGet("/api/status", () => new
{
    service = "GodMode.Server",
    version = "1.0.0",
    status = "running"
}).AllowAnonymous();

app.MapGet("/health", () => new { status = "healthy" }).AllowAnonymous();

// ── React client API surface (matches MAUI LocalServer) ────────

// Server list: return this server as the only entry
app.MapGet("/servers", () => new[]
{
    new ServerInfo("self", "Local Server", "local", ServerState.Running)
});

// SSE event stream (placeholder — no dynamic server changes in single-server mode)
app.MapGet("/events", async (HttpContext ctx) =>
{
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";
    await ctx.Response.Body.FlushAsync();
    // Keep connection open until client disconnects
    try { await Task.Delay(Timeout.Infinite, ctx.RequestAborted); }
    catch (OperationCanceledException) { }
});

var hub = app.MapHub<ProjectHub>("/hubs/projects");
if (requireAuth)
    hub.RequireAuthorization();

// SPA fallback: serve index.html for non-API/non-hub routes (React client routing)
app.MapFallbackToFile("index.html");

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
