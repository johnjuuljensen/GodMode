using Microsoft.AspNetCore.Authentication;
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

// Detect auth mode (exactly one, first match wins)
var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
var isCodespace = string.Equals(
    Environment.GetEnvironmentVariable("CODESPACES"), "true", StringComparison.OrdinalIgnoreCase);
var apiKey = builder.Configuration["Authentication:ApiKey"];

var authMode = !string.IsNullOrEmpty(googleClientId) ? "google"
    : isCodespace                                     ? "codespace"
    : !string.IsNullOrEmpty(apiKey)                   ? "apikey"
    :                                                   "none";

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

// CORS: not needed in production (React is same-origin, MAUI proxy is server-to-server).
// Only allow cross-origin in development (vite dev server on a different port).
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials()
                  .WithOrigins("http://localhost:5173", "https://localhost:5173");
        });
    });
}

builder.Services.AddHttpClient();

switch (authMode)
{
    case "google":
        builder.Services.AddGoogleAuth(builder.Configuration);
        break;
    case "codespace" or "apikey":
        builder.Services.AddAuthentication(GodModeAuthExtensions.SchemeName).AddGodModeAuth();
        builder.Services.AddAuthorization();
        break;
}

// Register application services
builder.Services.AddSingleton<IClaudeProcessManager, ClaudeProcessManager>();
builder.Services.AddSingleton<IStatusUpdater, StatusUpdater>();
builder.Services.AddSingleton<IRootConfigReader, RootConfigReader>();
builder.Services.AddSingleton<IScriptRunner, ScriptRunner>();
builder.Services.AddSingleton<ConfigFileWriter>();
builder.Services.AddSingleton<IProjectManager, ProjectManager>();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
    app.UseCors();

if (authMode != "none")
{
    app.UseAuthentication();
    app.UseAuthorization();
}

// Serve the React client from wwwroot/ (if present)
app.UseDefaultFiles();
app.UseStaticFiles();

// ── Auth endpoints (/api/auth/*) ───────────────────────────────

var auth = app.MapGroup("/api/auth");

// Challenge: tells the React client what auth method is required
auth.MapGet("/challenge", (HttpContext ctx, IServiceProvider sp) =>
{
    var googleOptions = sp.GetService<GoogleAuthOptions>();
    return Results.Ok(new
    {
        method = authMode,
        clientId = googleOptions?.ClientId,
        authenticated = ctx.User.Identity?.IsAuthenticated == true || authMode == "none",
    });
}).AllowAnonymous();

// Logout (shared across auth methods)
auth.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync();
    return Results.Ok(new { success = true });
}).AllowAnonymous();

// Method-specific endpoints
if (authMode == "google")
    auth.MapGoogleLoginEndpoint();

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
if (authMode != "none")
    hub.RequireAuthorization();

// SPA fallback: serve index.html for non-API/non-hub routes (React client routing)
app.MapFallbackToFile("index.html");

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
