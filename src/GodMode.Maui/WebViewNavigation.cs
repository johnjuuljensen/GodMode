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
            Logger.LogWarning("Dropped a navigation to {Address} (new window: {NewWindow})", ForLog(address), newWindow);
        return true;
    }

    private static async Task OpenOutsideAsync(Uri address)
    {
        var logged = ForLog(address.OriginalString);
        try
        {
            if (await Launcher.Default.OpenAsync(address))
                Logger.LogInformation("Opened {Address} in the system browser", logged);
            else
                Logger.LogWarning("Nothing opened {Address}", logged);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not open {Address} outside the app", logged);
        }
    }

    /// <summary>
    /// What a log line says about an address: an http or https one's origin, any other's scheme. A link in Claude's
    /// output can carry a token (a presigned URL) in its path, query or user info, so none of those is logged.
    /// </summary>
    private static string ForLog(string? address) =>
        !Uri.TryCreate(address, UriKind.Absolute, out var uri) ? address is null ? "no address" : "an address that is not absolute"
        : uri.Scheme is "http" or "https" ? uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)
        : $"a {uri.Scheme}: address";
}
