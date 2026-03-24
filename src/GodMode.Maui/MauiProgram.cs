namespace GodMode.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
#if WINDOWS
        // WebView2 serves our own React app from https://0.0.0.1 which is cross-origin
        // to any GodMode.Server. Disable web security so SignalR can connect freely.
        var args = "--disable-web-security";
#if DEBUG
        args += " --auto-open-devtools-for-tabs";
#endif
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", args);
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
