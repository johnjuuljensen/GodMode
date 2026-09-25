using GodMode.Server.Auth;
using GodMode.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// GodMode's MCP endpoint for a <see cref="LifecycleHarness"/>, mapped as Program.cs maps it (the
/// permission prompt, behind project tokens), on a loopback port of its own, so the fake's
/// permission prompt reaches the harness's server as claude's reaches the real one. Each call goes
/// to the server the harness runs at the time: after a restart, the new one.
/// </summary>
internal sealed class McpHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    /// <param name="logs">Where its warnings and errors go: the harness's, so a failing test shows them.</param>
    public McpHost(Func<IProjectManager> projects, ILoggerProvider logs)
    {
        Url = $"http://127.0.0.1:{ServerProcess.GetFreePort()}";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(Url);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddHttpClient();
        builder.Services.AddTransient(_ => projects());
        builder.Services.AddGodModeAuth(new AuthSettings(AuthMode.ApiKey, ApiKey: "harness-api-key-that-nobody-presents"));
        builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = ProjectManager.McpServerName, Version = "1.0.0" })
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<PermissionPromptTool>();
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapMcp(McpEndpointUrl.Path).RequireAuthorization(GodModeAuthExtensions.ProjectPolicy);
        _app.StartAsync().GetAwaiter().GetResult();
    }

    /// <summary>The address it listens on, which the harness's server is configured with (<c>Urls</c>).</summary>
    public string Url { get; }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
