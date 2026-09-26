using Microsoft.Web.WebView2.Core;

namespace GodMode.Maui;

/// <summary>Windows' side of <see cref="WebViewNavigation"/>: WebView2 asks before each navigation and each new window.</summary>
internal static class KeepOnApp
{
    public static void Attach(CoreWebView2 webView)
    {
        webView.NavigationStarting += (_, e) => e.Cancel = WebViewNavigation.Intercept(e.Uri, newWindow: false);
        webView.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            WebViewNavigation.Intercept(e.Uri, newWindow: true);
        };
    }
}
