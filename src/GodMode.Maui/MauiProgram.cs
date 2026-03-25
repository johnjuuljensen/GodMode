using GodMode.ClientBase;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GodMode.Maui;

public static class MauiProgram
{
    internal static string LocalBaseUrl { get; private set; } = "";
    internal static ILoggerFactory LoggerFactory { get; private set; } = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;

    public static MauiApp CreateMauiApp()
    {
        // Build services and start local HTTP/WebSocket listener
        var services = new ServiceCollection();
        services.AddGodModeClientServices();
        // Add debug window sink (MAUI-specific)
        services.AddLogging(builder => builder.AddDebug());
        var serviceProvider = services.BuildServiceProvider();

        LoggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var logger = LoggerFactory.CreateLogger("GodMode.Maui");
        logger.LogInformation("Starting GodMode MAUI app");

        var server = new LocalServer(serviceProvider);
        server.Start();
        LocalBaseUrl = server.BaseUrl;
        logger.LogInformation("LocalServer listening on {BaseUrl}", server.BaseUrl);


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
