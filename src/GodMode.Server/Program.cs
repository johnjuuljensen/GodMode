using GodMode.Server.Auth;
using GodMode.Server.Hubs;
using GodMode.Server.Services;
using GodMode.Shared;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using ModelContextProtocol.AspNetCore;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) =>
    configuration
        .ReadFrom.Configuration(context.Configuration)
        // Serilog ignores Logging:LogLevel. ASP.NET Core's Information events log full request
        // URLs, which for the hub's WebSocket upgrade carry the key as ?access_token=, so they
        // stay off after any configured levels.
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(".godmode-logs", "server-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 31));

// Select the auth mode (codespace > API key > loopback-only); refuse to start exposed without auth.
AuthSettings authSettings;
try
{
    authSettings = AuthModeSelector.Resolve(builder.Configuration);
}
catch (AuthConfigurationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

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

builder.Services.ConfigureHttpJsonOptions(options =>
{
    var defaults = JsonDefaults.Options;
    options.SerializerOptions.PropertyNamingPolicy = defaults.PropertyNamingPolicy;
    options.SerializerOptions.DefaultIgnoreCondition = defaults.DefaultIgnoreCondition;
    foreach (var converter in defaults.Converters)
        options.SerializerOptions.Converters.Add(converter);
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

builder.Services.AddGodModeAuth(authSettings);

// Register application services
builder.Services.AddSingleton<IClaudeProcessManager, ClaudeProcessManager>();
builder.Services.AddSingleton<IStatusUpdater, StatusUpdater>();
builder.Services.AddSingleton<ProjectLifecycle>();
builder.Services.AddSingleton<IRootConfigReader, RootConfigReader>();
builder.Services.AddSingleton<IScriptRunner, ScriptRunner>();
builder.Services.AddSingleton<ProfileFileManager>();
builder.Services.AddSingleton<IProjectManager, ProjectManager>();

// GodMode's MCP endpoint, for its sessions' claude: the permission prompt is its only tool. Stateless:
// claude's calls need no session (Claude Code speaks the sessionless 2026-07-28 revision), and a
// waiting call keeps its own response stream, which carries its progress and its answer
builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = ProjectManager.McpServerName, Version = "1.0.0" })
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<PermissionPromptTool>();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
    app.UseCors();

// Serve the React client from wwwroot/ (if present). Registered ahead of authentication on purpose:
// the page has to load before the user has entered the API key, and wwwroot holds only the built bundle.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/status", () => new
{
    service = "GodMode.Server",
    version = "1.0.0",
    status = "running"
});

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

app.MapHub<ProjectHub>(GodModeAuthExtensions.HubPath).RequireAuthorization();

// ── MCP (a project's claude → server, project-token auth): the permission prompt, its one tool ──

app.MapMcp(McpEndpointUrl.Path).RequireAuthorization(GodModeAuthExtensions.ProjectPolicy);

// SPA fallback: serve index.html for non-API/non-hub routes (React client routing).
// Anonymous for the same reason as the static files above: it is the client bundle's entry page.
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Logger.LogInformation("Authentication mode: {AuthMode}", authSettings.Mode);
if (authSettings.Mode == AuthMode.Loopback)
    app.Logger.LogWarning("No API key configured: unauthenticated access is allowed from loopback only. " +
        "Set {Setting} before binding to any other address.", AuthModeSelector.ApiKeySetting);

// Recover existing projects AFTER server starts (non-blocking), then carry on with those the last
// shutdown interrupted: a resume's MCP endpoint URL is an address the server is bound to by now
var projectManager = app.Services.GetRequiredService<IProjectManager>();
app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(async () =>
    {
        try
        {
            await projectManager.RecoverProjectsAsync();
            await projectManager.ResumeInterruptedProjectsAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error recovering projects: {ex.Message}");
        }
    });
});

app.Run();
return 0;
