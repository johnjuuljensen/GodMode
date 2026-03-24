using GodMode.Maui.Bridge;

namespace GodMode.Maui;

public partial class MainPage : ContentPage
{
    private readonly HostBridge _bridge;

    public MainPage()
    {
        InitializeComponent();
        _bridge = new HostBridge(WebView);

#if WINDOWS
        WebView.HandlerChanged += (s, e) =>
        {
            if (WebView.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.WebView2 wv2)
            {
                wv2.CoreWebView2Initialized += (_, _) =>
                {
                    wv2.CoreWebView2.Settings.AreDevToolsEnabled = true;
                };
            }
        };
#endif
    }

    private void WebView_RawMessageReceived(object? sender, HybridWebViewRawMessageReceivedEventArgs e)
    {
        if (e.Message is not null)
            _bridge.HandleMessageFromJs(e.Message);
    }
}
