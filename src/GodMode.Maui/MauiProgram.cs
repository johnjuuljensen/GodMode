using GodMode.ClientBase;
using GodMode.ClientBase.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SignalR.Proxy;

namespace GodMode.Maui;

public static class MauiProgram
{
    /// <summary>
    /// Origins the HybridWebView serves the React app from: https://0.0.0.1 on Android and Windows,
    /// app://0.0.0.1 on iOS and Mac Catalyst. Only these may use the relay.
    /// </summary>
    private static readonly string[] WebViewOrigins = ["https://0.0.0.1", "app://0.0.0.1"];

    internal static IServiceProvider Services { get; private set; } = null!;
    internal static ILoggerFactory LoggerFactory { get; private set; } = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;

    public static MauiApp CreateMauiApp()
    {
        // Build services and start local WebSocket relay
        var services = new ServiceCollection();
        services.AddGodModeClientServices();
        services.AddSingleton<ISecretStore, MauiSecretStore>();
        services.AddSingleton(sp =>
        {
            var directory = sp.GetRequiredService<IServerDirectory>();
            return new LocalServer(directory.ResolveAsync, WebViewOrigins, sp.GetRequiredService<ILoggerFactory>());
        });
        // Add debug window sink (MAUI-specific)
        services.AddLogging(builder => builder.AddDebug());
        Services = services.BuildServiceProvider();

        LoggerFactory = Services.GetRequiredService<ILoggerFactory>();
        var logger = LoggerFactory.CreateLogger("GodMode.Maui");
        logger.LogInformation("Starting GodMode MAUI app");

        var relay = Services.GetRequiredService<LocalServer>();
        relay.Start();
        logger.LogInformation("LocalServer listening on {BaseUrl}", relay.BaseUrl);

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        return builder.Build();
    }
}
