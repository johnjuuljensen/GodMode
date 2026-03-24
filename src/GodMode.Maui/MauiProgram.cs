using GodMode.Maui.Hubs;
using GodMode.Maui.Services;
using GodMode.Shared;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace GodMode.Maui;

public static class MauiProgram
{
    internal static string LocalHubUrl { get; private set; } = "";
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

        kestrelBuilder.Services.AddSingleton<ServerConnectionManager>();

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

        // SignalR hub — React connects per server: /hubs/projects?serverId=xxx
        app.MapHub<GodModeLocalHub>("/hubs/projects");

        // REST: server management
        app.MapGet("/servers", (ServerConnectionManager mgr) => mgr.ListServers());

        app.MapPost("/servers", (AddServerRequest req, ServerConnectionManager mgr) => mgr.AddServer(req));

        app.MapPut("/servers/{serverId}", (string serverId, AddServerRequest req, ServerConnectionManager mgr) =>
        {
            mgr.UpdateServer(serverId, req);
            return Results.Ok();
        });

        app.MapDelete("/servers/{serverId}", async (string serverId, ServerConnectionManager mgr) =>
        {
            await mgr.RemoveServer(serverId);
            return Results.Ok();
        });

        app.MapPost("/servers/{serverId}/connect", async (string serverId, ServerConnectionManager mgr) =>
        {
            await mgr.ConnectServer(serverId);
            return Results.Ok();
        });

        app.MapPost("/servers/{serverId}/disconnect", async (string serverId, ServerConnectionManager mgr) =>
        {
            await mgr.DisconnectServer(serverId);
            return Results.Ok();
        });

        // Load saved servers and start
        var connectionManager = app.Services.GetRequiredService<ServerConnectionManager>();
        connectionManager.LoadRegistry();

        app.StartAsync().GetAwaiter().GetResult();

        var address = app.Urls.First();
        LocalBaseUrl = address;
        LocalHubUrl = $"{address}/hubs/projects";
    }
}
