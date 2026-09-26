using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GodMode.Relay.Tests;

/// <summary>
/// A stand-in GodMode server on a random loopback port: /health and a SignalR hub at /hubs/projects
/// whose Echo method answers with this server's name. Records what each hub connection presented.
/// Given a key, it refuses any hub request that does not present it with 401, as GodMode.Server does.
/// </summary>
internal sealed class FakeUpstream : IAsyncDisposable
{
    private readonly WebApplication _app;

    public string Name { get; }
    public string Url { get; }

    /// <summary>Authorization header and query string of each hub request (negotiate and WebSocket).</summary>
    public ConcurrentQueue<(string? Authorization, string Query)> HubRequests { get; } = new();

    private FakeUpstream(string name, WebApplication app, string url)
    {
        Name = name;
        _app = app;
        Url = url;
    }

    /// <param name="url">Where it listens: a random loopback port by default, or the URL of one that died, to restart it.</param>
    public static async Task<FakeUpstream> StartAsync(string name, string? requiredKey = null, string url = "http://127.0.0.1:0")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(new UpstreamName(name));
        var app = builder.Build();

        FakeUpstream? self = null;
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/hubs/projects"))
            {
                var authorization = ctx.Request.Headers.Authorization.FirstOrDefault();
                self!.HubRequests.Enqueue((authorization, ctx.Request.QueryString.Value ?? ""));
                if (requiredKey != null && authorization != $"Bearer {requiredKey}")
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }
            await next();
        });
        app.MapGet("/health", () => Results.Ok());
        app.MapHub<EchoHub>("/hubs/projects");
        await app.StartAsync();

        var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.Single().TrimEnd('/');
        self = new FakeUpstream(name, app, bound);
        return self;
    }

    /// <summary>Dies: every connection is aborted at once, with no graceful shutdown (a server killed, or its host gone).</summary>
    public async Task KillAsync()
    {
        await _app.StopAsync(new CancellationToken(canceled: true));
        await _app.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed record UpstreamName(string Value);

    private sealed class EchoHub(UpstreamName name) : Hub
    {
        public string Echo(string text) => $"{name.Value}:{text}";
    }
}
