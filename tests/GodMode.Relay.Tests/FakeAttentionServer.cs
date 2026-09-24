using System.Collections.Concurrent;
using GodMode.Shared;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
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
/// A stand-in GodMode server's attention side on a random loopback port: /health, and a hub at /hubs/projects
/// with GetAttention and the AttentionChanged push, using the server's payload conventions (JsonDefaults).
/// </summary>
internal sealed class FakeAttentionServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly AttentionState _state;

    public string Url { get; }

    /// <summary>The access_token each hub request presented.</summary>
    public ConcurrentQueue<string?> Tokens { get; } = new();

    public int Connections => _state.Connections;

    private FakeAttentionServer(WebApplication app, AttentionState state, string url)
    {
        _app = app;
        _state = state;
        Url = url;
    }

    public static async Task<FakeAttentionServer> StartAsync(params AttentionItem[] items)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR().AddJsonProtocol(options =>
        {
            var defaults = JsonDefaults.Options;
            options.PayloadSerializerOptions.PropertyNamingPolicy = defaults.PropertyNamingPolicy;
            options.PayloadSerializerOptions.DefaultIgnoreCondition = defaults.DefaultIgnoreCondition;
            foreach (var converter in defaults.Converters)
                options.PayloadSerializerOptions.Converters.Add(converter);
        });
        var state = new AttentionState { Items = items };
        builder.Services.AddSingleton(state);
        var app = builder.Build();

        FakeAttentionServer? self = null;
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/hubs/projects"))
                self!.Tokens.Enqueue(ctx.Request.Query["access_token"].FirstOrDefault()
                    ?? ctx.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", ""));
            await next();
        });
        app.MapGet("/health", () => Results.Ok());
        app.MapHub<AttentionHub>("/hubs/projects");
        await app.StartAsync();

        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.Single().TrimEnd('/');
        self = new FakeAttentionServer(app, state, url);
        return self;
    }

    /// <summary>The list is now <paramref name="items"/>: pushes AttentionChanged to every connection, as the server does.</summary>
    public async Task SetAsync(params AttentionItem[] items)
    {
        _state.Items = items;
        await _app.Services.GetRequiredService<IHubContext<AttentionHub, IProjectHubClient>>().Clients.All.AttentionChanged(items);
    }

    /// <summary>Changes the list without a push, as while a client is disconnected.</summary>
    public void SetQuietly(params AttentionItem[] items) => _state.Items = items;

    /// <summary>Ends every hub connection from the server side.</summary>
    public void DropConnections()
    {
        foreach (var connection in _state.Open.Values)
            connection.Abort();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed class AttentionState
    {
        public volatile AttentionItem[] Items = [];
        public ConcurrentDictionary<string, HubCallerContext> Open { get; } = new();
        public int Connections;
    }

    private sealed class AttentionHub(AttentionState state) : Hub<IProjectHubClient>
    {
        public override Task OnConnectedAsync()
        {
            state.Open[Context.ConnectionId] = Context;
            Interlocked.Increment(ref state.Connections);
            return Task.CompletedTask;
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            state.Open.TryRemove(Context.ConnectionId, out _);
            return Task.CompletedTask;
        }

        public AttentionItem[] GetAttention() => state.Items;
    }
}
