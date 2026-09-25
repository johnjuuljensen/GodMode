using Android.Graphics;
using Android.OS;
using Android.Webkit;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using AWebView = Android.Webkit.WebView;

namespace GodMode.Maui;

/// <summary>
/// Android's side of <see cref="WebViewNavigation"/>. MAUI's HybridWebView client, which serves the app, checks each
/// navigation the page starts. A new window (a target=_blank link: MAUI turns multiple windows on, and without a
/// chrome client the tap did nothing) is caught and handed on.
/// </summary>
internal static class KeepOnApp
{
    public static void Attach(HybridWebViewHandler handler)
    {
        handler.PlatformView.SetWebViewClient(new AppClient(handler));
        handler.PlatformView.SetWebChromeClient(new NewWindows());
    }

    private sealed class AppClient(HybridWebViewHandler handler) : MauiHybridWebViewClient(handler)
    {
        public override bool ShouldOverrideUrlLoading(AWebView? view, IWebResourceRequest? request) =>
            WebViewNavigation.Intercept(request?.Url?.ToString(), newWindow: false);
    }

    /// <summary>
    /// A new window's address is known only once the window starts loading it, so a throwaway WebView stands in for
    /// the window: it takes the first address it is sent to, and goes.
    /// </summary>
    private sealed class NewWindows : WebChromeClient
    {
        public override bool OnCreateWindow(AWebView? view, bool isDialog, bool isUserGesture, Message? resultMsg)
        {
            if (view?.Context is not { } context || resultMsg?.Obj is not AWebView.WebViewTransport transport)
                return false;
            var window = new AWebView(context);
            window.SetWebViewClient(new NewWindowClient());
            transport.WebView = window;
            resultMsg.SendToTarget();
            return true;
        }
    }

    private sealed class NewWindowClient : WebViewClient
    {
        private bool _taken;

        public override bool ShouldOverrideUrlLoading(AWebView? view, IWebResourceRequest? request)
        {
            Take(view, request?.Url?.ToString());
            return true;
        }

        public override void OnPageStarted(AWebView? view, string? url, Bitmap? favicon) => Take(view, url);

        private void Take(AWebView? view, string? url)
        {
            if (_taken || url is null or "about:blank") return;
            _taken = true;
            WebViewNavigation.Intercept(url, newWindow: true);
            if (view is null) return;
            view.StopLoading();
            // After this callback returns, not inside it. The stand-in is never attached, and a detached view's own
            // Post waits for an attach (Android 7+), so it would never run
            new Handler(Looper.MainLooper!).Post(view.Destroy);
        }
    }
}
