using GodMode.ClientBase;
using GodMode.ClientBase.Services;
using GodMode.Maui.Voice;
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
        // The voice session belongs to the app, so it outlives a page
        services.AddSingleton<VoiceHost>();
        // Add debug window sink (MAUI-specific)
        services.AddLogging(builder => builder.AddDebug());
        Services = services.BuildServiceProvider();

        LoggerFactory = Services.GetRequiredService<ILoggerFactory>();
        var logger = LoggerFactory.CreateLogger("GodMode.Maui");
        logger.LogInformation("Starting GodMode MAUI app");

#if WINDOWS
        // Before the relay, voice or a window: a release build that finds the app running hands off to it and exits
        // (issue #339). Building the container above starts none of them
        if (!SingleInstance.Claim(Environment.GetCommandLineArgs()[1..], logger))
            Environment.Exit(0);
#endif

        var relay = Services.GetRequiredService<LocalServer>();
        relay.Start();
        logger.LogInformation("LocalServer listening on {BaseUrl}", relay.BaseUrl);
#if WINDOWS
        // What interrupts makes a sound while the app runs (issue #438); Android's service does this on the phone
        WindowsAttention.Start(Services, LoggerFactory);
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
}
