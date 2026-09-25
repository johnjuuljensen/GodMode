using GodMode.ClientBase.Bridge;
using Microsoft.Extensions.Logging;

namespace GodMode.Maui;

/// <summary>
/// Keeps the WebView on the app. A navigation to one of the app's pages goes ahead; one to an http or https address
/// anywhere else is cancelled, and the system browser opens it; any other is cancelled. Each platform's WebView asks
/// here before it navigates or opens a window (KeepOnApp in Platforms/Android and Platforms/Windows).
/// </summary>
internal static class WebViewNavigation
{
    private static ILogger Logger => MauiProgram.LoggerFactory.CreateLogger("WebView");

    /// <summary>
    /// True when the WebView must not make this navigation itself: it goes anywhere but one of the app's pages,
    /// or it opens a new window, which never shows the app. An http or https address opens outside the app instead.
    /// </summary>
    public static bool Intercept(string? address, bool newWindow)
    {
        var destination = AppOrigin.DestinationOf(address);
        if (destination == WebViewDestination.App && !newWindow) return false;
        if (destination == WebViewDestination.Browser)
            _ = OpenOutsideAsync(new Uri(address!));
        else
            Logger.LogWarning("Dropped a navigation to {Address} (new window: {NewWindow})", address ?? "no address", newWindow);
        return true;
    }

    private static async Task OpenOutsideAsync(Uri address)
    {
        try
        {
            if (await Launcher.Default.OpenAsync(address))
                Logger.LogInformation("Opened {Address} in the system browser", address);
            else
                Logger.LogWarning("Nothing opened {Address}", address);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not open {Address} outside the app", address);
        }
    }
}
