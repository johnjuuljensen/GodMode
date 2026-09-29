using GodMode.Maui.Bridge;
using Microsoft.Extensions.Logging;

namespace GodMode.Maui;

public partial class MainPage : ContentPage
{
    private readonly ShellBridge _shell;

    /// <summary>The page of the main window (no profile), or of a profile's own window, locked to it (#340).</summary>
    public MainPage(string? profile)
    {
        Profile = profile;
        InitializeComponent();

        // React asks the shell for the relay's URL and secret over the bridge (relay.info), and which window it is in
        // (window.info)
        _shell = ShellBridge.Attach(WebView, profile, MauiProgram.Services);

        // The WebView shows only the app: a link opens outside it (WebViewNavigation)
#if ANDROID
        WebView.HandlerChanged += (_, _) =>
        {
            if (WebView.Handler is Microsoft.Maui.Handlers.HybridWebViewHandler handler)
                KeepOnApp.Attach(handler);
        };
#endif

#if WINDOWS
        WebView.HandlerChanged += (_, _) =>
        {
            if (WebView.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.WebView2 wv2)
            {
                wv2.CoreWebView2Initialized += async (_, _) =>
                {
                    KeepOnApp.Attach(wv2.CoreWebView2);
                    wv2.CoreWebView2.Settings.AreDevToolsEnabled = true;
                    // The page's title is the window's: "(2) GodMode — Work", the profile and what needs the user there
                    wv2.CoreWebView2.DocumentTitleChanged += (_, _) => ShowTitle(wv2.CoreWebView2.DocumentTitle);

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

    /// <summary>The profile the page's window is locked to; null in the main window.</summary>
    public string? Profile { get; }

    /// <summary>The window closed: its page's bridge lets go of the app's events.</summary>
    public void Detach() => _shell.Dispose();

    /// <summary>The page's title in the window's title bar, and the taskbar's. Not the address it has before it loads.</summary>
    private void ShowTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Contains("://") || Window is not { } window) return;
        window.Title = title;
        if (window.TitleBar is TitleBar bar) bar.Title = title;
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

    /// <summary>The developer tools of the page the WebView shows, where the platform has them (Windows).</summary>
    public static void OpenDevTools(HybridWebView webView)
    {
#if WINDOWS
        webView.Dispatcher.Dispatch(() =>
        {
            if (webView.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.WebView2 wv2
                && wv2.CoreWebView2 != null)
            {
                wv2.CoreWebView2.OpenDevToolsWindow();
            }
        });
#endif
    }
}
