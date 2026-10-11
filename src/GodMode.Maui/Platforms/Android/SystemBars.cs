namespace GodMode.Maui;

/// <summary>
/// Android 15 lays every app out edge to edge, under the status bar and the gesture bar (#258). A WebView from M136
/// passes those bars to the page as env(safe-area-inset-*), and from M139 resizes it for the keyboard, so React keeps
/// clear of them itself and the page's colours run under the bars. An older WebView passes neither (its insets are 0,
/// and the keyboard covers the page's bottom): there the page itself keeps clear of the bars and the keyboard, and React
/// sees no insets.
/// </summary>
internal static class SystemBars
{
    private const int FirstWebViewWithInsets = 139;

    /// <summary>The page's safe area: none where the WebView passes the insets on, else the bars and the keyboard.</summary>
    public static SafeAreaEdges PageEdges =>
        WebViewMajorVersion() >= FirstWebViewWithInsets ? SafeAreaEdges.None : SafeAreaEdges.All;

    /// <summary>The installed WebView's major version, 0 when it is not known (before API 26, or no WebView).</summary>
    private static int WebViewMajorVersion() =>
        OperatingSystem.IsAndroidVersionAtLeast(26)
        && Android.Webkit.WebView.CurrentWebViewPackage?.VersionName?.Split('.')[0] is { } major
        && int.TryParse(major, out var version)
            ? version
            : 0;
}
