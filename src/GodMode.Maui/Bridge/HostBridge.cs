using GodMode.ClientBase.Bridge;
using Microsoft.Extensions.Logging;

namespace GodMode.Maui.Bridge;

/// <summary>
/// The bridge (<see cref="BridgeChannel"/>) over HybridWebView's raw message channel. It answers only the app's
/// own page, and the address it goes by is the page the platform's WebView shows.
/// </summary>
public sealed class HostBridge : BridgeChannel
{
    private readonly HybridWebView _webView;

    public HostBridge(HybridWebView webView, ILogger logger) : base(logger)
    {
        _webView = webView;
        // Android raises it on its JavaBridge thread; Windows on the UI thread, inside WebView2's WebMessageReceived
        _webView.RawMessageReceived += (_, e) =>
        {
            if (e.Message is { } message)
                Receive(message);
        };
    }

    // MAUI's handler takes WebView2's WebMessageReceivedEventArgs and passes on only the message. That event is
    // raised for the top-level document, and the channel reads this on the UI thread while it is being raised
    protected override string? PageAddress => _webView.Handler?.PlatformView switch
    {
#if ANDROID
        Android.Webkit.WebView view => view.Url,
#elif WINDOWS
        Microsoft.UI.Xaml.Controls.WebView2 view => view.CoreWebView2?.Source,
#elif IOS || MACCATALYST
        WebKit.WKWebView view => view.Url?.AbsoluteString,
#endif
        _ => null,
    };

    protected override void PostToPage(string message) => _webView.SendRawMessage(message);

    protected override void OnUiThread(Action action) => MainThread.BeginInvokeOnMainThread(action);
}
