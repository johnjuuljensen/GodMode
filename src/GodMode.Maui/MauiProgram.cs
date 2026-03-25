using GodMode.ClientBase;
using GodMode.ClientBase.Services;
using GodMode.ClientBase.Services.Models;
using GodMode.Maui.Hubs;
using GodMode.Shared;
using GodMode.Shared.Hubs;
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

        kestrelBuilder.Services.AddGodModeClientServices();
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

        app.MapHub<GodModeLocalHub>("/hubs/projects");

        MapServerEndpoints(app);

        app.StartAsync().GetAwaiter().GetResult();
        LocalBaseUrl = app.Urls.First();
    }

    private static void MapServerEndpoints(WebApplication app)
    {
        var servers = app.MapGroup("/servers");

        servers.MapGet("/", async (IServerRegistryService registry, IHostConnectionService connections) =>
        {
            var list = await registry.GetServersAsync();
            return list.Select((s, i) => new ServerInfo(
                i.ToString(),
                s.DisplayName ?? s.Url ?? "Server",
                s.Url ?? "",
                connections.IsConnected(i.ToString()) ? "connected" : "disconnected"
            ));
        });

        servers.MapPost("/", async (AddServerRequest req, IServerRegistryService registry) =>
        {
            var registration = new ServerRegistration
            {
                Type = "local",
                Url = req.Url,
                DisplayName = req.DisplayName,
                Token = !string.IsNullOrEmpty(req.AccessToken)
                    ? registry.EncryptToken(req.AccessToken)
                    : null
            };
            await registry.AddServerAsync(registration);

            var list = await registry.GetServersAsync();
            return new ServerInfo((list.Count - 1).ToString(), req.DisplayName, req.Url, "disconnected");
        });

        servers.MapDelete("/{index:int}", async (int index, IServerRegistryService registry, IHostConnectionService connections) =>
        {
            // TODO: disconnect any active connections for this server's hosts
            await registry.RemoveServerAsync(index);
            return Results.Ok();
        });

        // Host-level operations (hostId comes from the provider, e.g., "local-server" or codespace name)
        var hosts = app.MapGroup("/hosts");

        hosts.MapGet("/", async (IHostConnectionService connections) =>
            await connections.ListAllHostsAsync());

        hosts.MapPost("/{hostId}/start", async (string hostId, IHostConnectionService connections) =>
        {
            var providers = await connections.GetAllProvidersAsync();
            foreach (var (provider, _) in providers)
            {
                var hostList = await provider.ListHostsAsync();
                if (hostList.Any(h => h.Id == hostId))
                {
                    await provider.StartHostAsync(hostId);
                    return Results.Ok();
                }
            }
            return Results.NotFound();
        });

        hosts.MapPost("/{hostId}/stop", async (string hostId, IHostConnectionService connections) =>
        {
            var providers = await connections.GetAllProvidersAsync();
            foreach (var (provider, _) in providers)
            {
                var hostList = await provider.ListHostsAsync();
                if (hostList.Any(h => h.Id == hostId))
                {
                    await provider.StopHostAsync(hostId);
                    return Results.Ok();
                }
            }
            return Results.NotFound();
        });

        hosts.MapPost("/{hostId}/connect", async (string hostId,
            IHostConnectionService connections, IHubContext<GodModeLocalHub> hubContext) =>
        {
            var connection = await connections.ConnectToHostAsync(hostId);

            // Forward all IProjectHubClient callbacks to local React clients in this host's group
            foreach (var method in typeof(IProjectHubClient).GetMethods())
            {
                var methodName = method.Name;
                var paramTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();
                connection.On(methodName, paramTypes, (args, _) =>
                    hubContext.Clients.Group($"server-{hostId}").SendCoreAsync(methodName, args!),
                    new object());
            }

            return Results.Ok();
        });

        hosts.MapPost("/{hostId}/disconnect", async (string hostId, IHostConnectionService connections) =>
        {
            await connections.DisconnectFromHostAsync(hostId);
            return Results.Ok();
        });
    }
}
