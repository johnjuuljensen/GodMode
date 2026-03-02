using GodMode.Server.Auth;
using GodMode.Server.Hubs;
using GodMode.Server.Services;
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
var requireAuth = isCodespace || !string.IsNullOrEmpty(apiKey);

// Add services to the container
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.PropertyNamingPolicy = null;
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

if (requireAuth)
{
    builder.Services.AddHttpClient();
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
var authMode = isCodespace ? "Codespace (GitHub PAT)" :
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
