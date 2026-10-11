namespace GodMode.Maui;

/// <summary>
/// The app is laid out edge to edge, under the status bar and the gesture bar (#258). A WebView from M139 passes those
/// bars to the page as env(safe-area-inset-*) and resizes it for the keyboard, so React keeps clear of them itself and
/// the page's colours run under the bars. An older one passes neither (its insets are 0, and the keyboard covers the
/// page's bottom): there the page keeps clear of the bars and the keyboard itself, and React sees no insets.
/// </summary>
internal static class SystemBars
{
    private const int FirstWebViewWithInsets = 139;

    /// <summary>The installed WebView passes the system bars and the keyboard on to the page.</summary>
    public static bool WebViewPassesInsets => WebViewMajorVersion() >= FirstWebViewWithInsets;

    /// <summary>The installed WebView's major version, 0 when it is not known (before API 26, or no WebView).</summary>
    private static int WebViewMajorVersion() =>
        OperatingSystem.IsAndroidVersionAtLeast(26)
        && Android.Webkit.WebView.CurrentWebViewPackage?.VersionName?.Split('.')[0] is { } major
        && int.TryParse(major, out var version)
            ? version
            : 0;
}
