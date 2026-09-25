using GodMode.Server;
using GodMode.Server.Auth;
using GodMode.Server.Hubs;
using GodMode.Server.Services;
using GodMode.Shared;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using ModelContextProtocol.AspNetCore;
using Serilog;
using Serilog.Events;

// Not the server: the helper a stop starts on Windows to interrupt a session in its own console
if (args is [SessionProcessTree.ConsoleBreakFlag, ..])
    return SessionProcessTree.RunConsoleBreakHelper(args);

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

// Select the auth mode (codespace, else the API key: configured, else generated into the key file) and
// the browser origins; every mode needs a credential. Refuse to start on configuration that cannot work
AuthSettings authSettings;
OriginPolicy originPolicy;
try
{
    authSettings = AuthModeSelector.Resolve(builder.Configuration);
    originPolicy = OriginPolicy.From(builder.Configuration, builder.Environment.IsDevelopment(),
        isCodespace: authSettings.Mode == AuthMode.Codespace);
    PermissionPromptTool.KeepAliveFrom(builder.Configuration);
}
catch (StartupConfigurationException ex)
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

// Configure the HTTP request pipeline. A browser's request from an origin other than the server's own
// is refused first, before static files, CORS and authentication
app.UseOriginPolicy(originPolicy);

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
if (authSettings is { KeyFilePath: { } keyFile, KeyFileCreated: false })
    app.Logger.LogInformation("No API key is configured: using the one in {KeyFile}", keyFile);
if (authSettings is { KeyFilePath: { } newKeyFile, KeyFileCreated: true })
{
    // Printed this once, to the console alone: never to the log file
    app.Lifetime.ApplicationStarted.Register(() => Console.WriteLine($"""

        GodMode.Server generated its API key, since none is configured ({AuthModeSelector.ApiKeySetting}):

            {authSettings.ApiKey}

        Every client needs it: enter it on the browser's key page, or as the server's API key when you add it in the app.
        It is kept in {newKeyFile}, readable by this user only, and used on every start.
        This is the only time it is printed.

        A browser is let in only from this server's own addresses (its log line "Browser requests are accepted from").
        One that opens it by a host name, a LAN address or another port (a container's published port) needs
        that origin in {OriginPolicy.AllowedOriginsSetting}.

        """));
}

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
