using GodMode.ClientBase;
using GodMode.Maui.Hubs;
using GodMode.Maui.Services;
using GodMode.Shared;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace GodMode.Maui;

public static class MauiProgram
{
    internal static string LocalBaseUrl { get; private set; } = "";

    public static MauiApp CreateMauiApp()
    {
        StartLocalHub();

#if WINDOWS && DEBUG
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
            "--auto-open-devtools-for-tabs");
#endif

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        return builder.Build();
    }

    private static void StartLocalHub()
    {
        var kestrelBuilder = WebApplication.CreateSlimBuilder();
        kestrelBuilder.WebHost.UseUrls("http://127.0.0.1:0");

        // ClientBase services (server registry with encrypted tokens, host providers)
        kestrelBuilder.Services.AddGodModeClientServices();

        // Local proxy services
        kestrelBuilder.Services.AddSingleton<ServerConnectionManager>();
        kestrelBuilder.Services.AddSingleton<IHubFilter, ProxyHubFilter>();

        kestrelBuilder.Services.AddSignalR()
            .AddJsonProtocol(options =>
            {
                var defaults = JsonDefaults.Options;
                options.PayloadSerializerOptions.PropertyNamingPolicy = defaults.PropertyNamingPolicy;
                options.PayloadSerializerOptions.DefaultIgnoreCondition = defaults.DefaultIgnoreCondition;
                foreach (var converter in defaults.Converters)
                    options.PayloadSerializerOptions.Converters.Add(converter);
            });

        kestrelBuilder.Services.AddCors(options =>
            options.AddDefaultPolicy(policy =>
                policy.AllowAnyHeader().AllowAnyMethod().AllowCredentials().SetIsOriginAllowed(_ => true)));

        var app = kestrelBuilder.Build();
        app.UseCors();

        // SignalR hub — React connects per server: /hubs/projects?serverId={index}
        app.MapHub<GodModeLocalHub>("/hubs/projects");

        // REST: server management
        var servers = app.MapGroup("/servers");

        servers.MapGet("/", async (ServerConnectionManager mgr) =>
            await mgr.ListServersAsync());

        servers.MapPost("/", async (AddServerRequest req, ServerConnectionManager mgr) =>
            await mgr.AddServerAsync(req));

        servers.MapDelete("/{index:int}", async (int index, ServerConnectionManager mgr) =>
        {
            await mgr.RemoveServerAsync(index);
            return Results.Ok();
        });

        servers.MapPost("/{index:int}/connect", async (int index, ServerConnectionManager mgr) =>
        {
            await mgr.ConnectServerAsync(index);
            return Results.Ok();
        });

        servers.MapPost("/{index:int}/disconnect", async (int index, ServerConnectionManager mgr) =>
        {
            await mgr.DisconnectServerAsync(index);
            return Results.Ok();
        });

        app.StartAsync().GetAwaiter().GetResult();

        LocalBaseUrl = app.Urls.First();
    }
}
