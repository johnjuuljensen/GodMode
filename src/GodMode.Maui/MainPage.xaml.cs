using GodMode.Maui.Bridge;
using Microsoft.Extensions.Logging;

namespace GodMode.Maui;

public partial class MainPage : ContentPage
{
    private static MainPage? _instance;

    public MainPage()
    {
        _instance = this;
        InitializeComponent();

        // React asks the shell for the relay's URL and secret over the bridge (relay.info).
        ShellBridge.Attach(WebView, MauiProgram.Services);

#if WINDOWS
        WebView.HandlerChanged += (_, _) =>
        {
            if (WebView.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.WebView2 wv2)
            {
                wv2.CoreWebView2Initialized += async (_, _) =>
                {
                    wv2.CoreWebView2.Settings.AreDevToolsEnabled = true;

                    var logger = MauiProgram.LoggerFactory.CreateLogger("WebView");

                    wv2.CoreWebView2.WebMessageReceived += (_, args) =>
                    {
                        try
                        {
                            var json = System.Text.Json.JsonDocument.Parse(args.WebMessageAsJson);
                            var root = json.RootElement;
                            if (root.TryGetProperty("type", out var type) && type.GetString() == "console")
                            {
                                var level = root.GetProperty("level").GetString();
                                var msg = root.GetProperty("message").GetString() ?? "";
                                switch (level)
                                {
                                    case "error": logger.LogError("[JS] {Message}", msg); break;
                                    case "warn": logger.LogWarning("[JS] {Message}", msg); break;
                                    case "debug": logger.LogDebug("[JS] {Message}", msg); break;
                                    default: logger.LogInformation("[JS] {Message}", msg); break;
                                }
                            }
                        }
                        catch { /* not our message format */ }
                    };

                    await wv2.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("""
                        (function() {
                            const orig = { log: console.log, warn: console.warn, error: console.error, info: console.info, debug: console.debug };
                            function hook(level) {
                                return function(...args) {
                                    orig[level].apply(console, args);
                                    try {
                                        const message = args.map(a => typeof a === 'object' ? JSON.stringify(a) : String(a)).join(' ');
                                        window.chrome.webview.postMessage({ type: 'console', level, message });
                                    } catch {}
                                };
                            }
                            console.log = hook('log');
                            console.warn = hook('warn');
                            console.error = hook('error');
                            console.info = hook('info');
                            console.debug = hook('debug');
                        })();
                        """);
                };
            }
        };
#endif
    }

    // Android back walks the client's history, where each screen is an entry. With none left
    // the default runs, which leaves the app.
    protected override bool OnBackButtonPressed()
    {
#if ANDROID
        if (WebView.Handler?.PlatformView is Android.Webkit.WebView webView && webView.CanGoBack())
        {
            webView.EvaluateJavascript("history.back()", null);
            return true;
        }
#endif
        return base.OnBackButtonPressed();
    }

    public static void OpenDevTools()
    {
#if WINDOWS
        _instance?.Dispatcher.Dispatch(() =>
        {
            if (_instance.WebView.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.WebView2 wv2
                && wv2.CoreWebView2 != null)
            {
                wv2.CoreWebView2.OpenDevToolsWindow();
            }
        });
#endif
    }
}
