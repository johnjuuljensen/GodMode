namespace GodMode.ClientBase.Bridge;

/// <summary>
/// Where the app's own pages are: HybridWebView serves the React app from <c>https://0.0.0.1</c> on Android and
/// Windows, and from <c>app://0.0.0.1</c> on iOS and Mac Catalyst. The WebView shows nothing else, and only these
/// pages may use the bridge.
/// </summary>
public static class AppOrigin
{
    public static readonly IReadOnlyList<string> All = ["https://0.0.0.1", "app://0.0.0.1"];

    /// <summary>The address is one of the app's own pages.</summary>
    public static bool Contains(string? address) =>
        Of(address) is { } origin && All.Contains(origin, StringComparer.OrdinalIgnoreCase);

    /// <summary>An absolute address's origin (<c>scheme://host[:port]</c>), or null when it has none.</summary>
    public static string? Of(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null;

    /// <summary>Where an address the WebView was asked to show goes.</summary>
    public static WebViewDestination DestinationOf(string? address) =>
        Contains(address) ? WebViewDestination.App
        : Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? WebViewDestination.Browser
        : WebViewDestination.Nowhere;
}

/// <summary>Where an address the WebView was asked to show goes (<see cref="AppOrigin.DestinationOf"/>).</summary>
public enum WebViewDestination
{
    /// <summary>One of the app's own pages: the WebView shows it.</summary>
    App,

    /// <summary>An http or https address anywhere else: the system browser opens it.</summary>
    Browser,

    /// <summary>Any other address (mailto:, intent:, file:, …): nothing opens it.</summary>
    Nowhere,
}
