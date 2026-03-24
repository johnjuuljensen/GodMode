namespace GodMode.Maui;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();

        // Inject the local hub base URL after the WebView loads
        Loaded += async (_, _) =>
        {
            // Small delay to ensure the WebView's JS runtime is ready
            await Task.Delay(200);
            await WebView.EvaluateJavaScriptAsync(
                $"window.__GODMODE_BASE_URL__ = '{MauiProgram.LocalBaseUrl}'");
        };

#if WINDOWS
        WebView.HandlerChanged += (_, _) =>
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
}
