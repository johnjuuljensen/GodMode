using GodMode.ClientBase;
using Microsoft.Extensions.DependencyInjection;

namespace GodMode.Maui;

public static class MauiProgram
{
    internal static string LocalBaseUrl { get; private set; } = "";

    public static MauiApp CreateMauiApp()
    {
        // Build services and start local HTTP/WebSocket listener
        var services = new ServiceCollection();
        services.AddGodModeClientServices();
        var serviceProvider = services.BuildServiceProvider();

        var server = new LocalServer(serviceProvider);
        server.Start();
        LocalBaseUrl = server.BaseUrl;

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
}
