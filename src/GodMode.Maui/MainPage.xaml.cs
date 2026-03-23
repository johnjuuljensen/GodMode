using GodMode.Maui.Bridge;

namespace GodMode.Maui;

public partial class MainPage : ContentPage
{
    private readonly HostBridge _bridge;

    public MainPage()
    {
        InitializeComponent();
        _bridge = new HostBridge(WebView);
    }

    private void WebView_RawMessageReceived(object? sender, HybridWebViewRawMessageReceivedEventArgs e)
    {
        if (e.Message is not null)
            _bridge.HandleMessageFromJs(e.Message);
    }
}
