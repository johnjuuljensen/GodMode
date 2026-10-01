using GodMode.Server;
using GodMode.Server.Auth;
using GodMode.Server.Hubs;
using GodMode.Server.Services;
using GodMode.Shared;
using Serilog;
using Serilog.Events;

// Not the server: the helper a stop starts on Windows to interrupt a session in its own console
if (args is [SessionProcessTree.ConsoleBreakFlag, ..])
    return SessionProcessTree.RunConsoleBreakHelper(args);

var builder = WebApplication.CreateBuilder(args);

// This instance's own config file, if one is named (--config, GODMODE_CONFIG): nothing per user is read.
// The server reads no user secrets either (it has no UserSecretsId), so every worktree's dev server
// starts on appsettings alone, with its own scratch roots, unless it is given a file of its own
string? instanceConfigFile;
try
{
    instanceConfigFile = InstanceConfig.AddTo(builder.Configuration, builder.Configuration, builder.Environment.EnvironmentName);
}
catch (StartupConfigurationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

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

// Select the auth mode (codespace, else the API key: configured, else generated into the key file);
// every mode needs a credential. Refuse to start on configuration that cannot work
AuthSettings authSettings;
try
{
    authSettings = AuthModeSelector.Resolve(builder.Configuration);
    PermissionPromptTool.KeepAliveFrom(builder.Configuration);
}
catch (StartupConfigurationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

// Add services to the container. A connection's calls run side by side, a few at a time: a reply
// that waits for a resumed claude to start its session (up to SessionStartTimeoutSeconds) leaves the
// tab its Stop and its subscribes. Subscribes still run one at a time per connection, in order
// (ProjectManager.SubscribeProjectAsync)
builder.Services.AddSignalR(options => options.MaximumParallelInvocationsPerClient = ProjectHub.ParallelInvocationsPerClient)
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

builder.Services.AddHttpClient();

builder.Services.AddGodModeAuth(authSettings);

// Register application services
builder.Services.AddSingleton<IClaudeProcessManager, ClaudeProcessManager>();
builder.Services.AddSingleton<IStatusUpdater, StatusUpdater>();
builder.Services.AddSingleton<ProjectLifecycle>();
builder.Services.AddSingleton<IRootConfigReader, RootConfigReader>();
builder.Services.AddSingleton<IScriptRunner, ScriptRunner>();
builder.Services.AddSingleton<IProjectManager, ProjectManager>();

// GodMode's MCP endpoints: its sessions' claude's (the permission prompt) and the fleet's (GodModeMcp)
builder.Services.AddGodModeMcp();

var app = builder.Build();

// Configure the HTTP request pipeline. The server serves no page and no browser is its client: a request
// with an Origin is refused first, before authentication
app.UseOriginPolicy();

app.UseAuthentication();
app.UseAuthorization();

// What this server is, with the key: the app's codespace probe (GitHubCodespaceProvider) asks here
app.MapGet("/", () => new
{
    service = "GodMode.Server",
    version = "1.0.0",
    status = "running"
});

app.MapGet("/health", () => new { status = "healthy" }).AllowAnonymous();

app.MapHub<ProjectHub>(GodModeAuthExtensions.HubPath).RequireAuthorization();

// ── MCP: /mcp (a project's claude, its project token): the permission prompt, its one tool;
// /mcp/fleet (an overseer: the server's own credential, or a granted session's project token): the fleet's tools ──

app.MapGodModeMcp();

app.Logger.LogInformation("Config file: {ConfigFile}", instanceConfigFile ?? "none (appsettings only)");
app.Logger.LogInformation("Authentication mode: {AuthMode}", authSettings.Mode);
if (authSettings is { KeyFilePath: { } keyFile, KeyFileCreated: false })
    app.Logger.LogInformation("No API key is configured: using the one in {KeyFile}", keyFile);
if (authSettings is { KeyFilePath: { } newKeyFile, KeyFileCreated: true })
{
    // Printed this once, to the console alone: never to the log file
    app.Lifetime.ApplicationStarted.Register(() => Console.WriteLine($"""

        GodMode.Server generated its API key, since none is configured ({AuthModeSelector.ApiKeySetting}):

            {authSettings.ApiKey}

        Enter it as the server's API key when you add the server in the GodMode app.
        It is kept in {newKeyFile}, readable by this user only, and used on every start.
        This is the only time it is printed.

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
