using GodMode.ClientBase.Layout;
using Microsoft.Extensions.Logging;

namespace GodMode.Maui;

/// <summary>
/// The app's windows (#340): the main window, which shows every profile, and on Windows one window per profile, locked
/// to it by name across every server. Each has its own page and page's bridge; the relay, voice, the server list and
/// attention stay the app's. Closing a profile window leaves the app running; closing the main window closes the app,
/// and the profile windows with it.
/// </summary>
internal static class AppWindows
{
    /// <summary>What a later start names to open a profile's window: <c>GodMode.Maui.exe --profile Work</c>.</summary>
    public const string ProfileArgument = "--profile";

    /// <summary>Whether the app opens profile windows here: Windows only. Android and iOS have the one window.</summary>
    public static bool CanOpen =>
#if WINDOWS
        true;
#else
        false;
#endif

    /// <summary>
    /// A window for the main page (no profile) or a profile's. Windows extends the page into the title bar, which shows
    /// nothing until it has a TitleBar: this gives it the app's icon and name (#313), in the dark themes' near-black,
    /// and the page's title once it has one ("Work (2) - GodMode", MainPage). Android and iOS have no title bar and
    /// ignore it. The icon is the MauiIcon's own output, appiconLogo.scale-*.png beside the exe, not a second copy.
    /// On Windows it opens at <paramref name="place"/> when it has one (a restart, #341), and its place is saved from then on.
    /// </summary>
    public static Window New(string? profile, WindowPlace? place = null)
    {
        var title = Title(profile);
        var page = new MainPage(profile);
        var window = new Window(page)
        {
            Title = title,
            TitleBar = new TitleBar
            {
                Title = title,
                Icon = "appiconlogo.png",
                BackgroundColor = Color.FromArgb("#0d0d14"),
                ForegroundColor = Color.FromArgb("#E0FFFFFF"),
            },
        };
#if WINDOWS
        // The main window is the app: closing it closes the profile windows too. A profile window closes alone.
        // WinUI's Closed, not MAUI's Destroying: with other windows open, closing one doesn't raise Destroying
        window.HandlerChanged += (_, _) =>
        {
            if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window platform) return;
            WindowPlaces.Opened(platform, profile, place);
            platform.Closed += (_, _) =>
            {
                page.Detach();
                if (profile is null)
                {
                    WindowPlaces.Quitting();
                    // Each by name: Quit alone has left a profile window open (on another desktop), and the app with it
                    foreach (var other in Application.Current?.Windows.Where(w => w != window).ToList() ?? [])
                        Application.Current!.CloseWindow(other);
                    Application.Current?.Quit();
                }
                else WindowPlaces.Closed(platform);
            };
        };
#endif
        return window;
    }

    /// <summary>
    /// A window's title before its page gives it one: "GodMode", or "Work - GodMode" for the Work profile's, the profile
    /// first so a narrow taskbar button still shows it (#355). The page's windowTitle (useAttentionTitle.ts) builds
    /// the same, with the count of what needs the user; a client test holds this one to it.
    /// </summary>
    public static string Title(string? profile) => profile is null ? "GodMode" : $"{profile} - GodMode";

    /// <summary>Opens the profile in a window of its own, or brings forward the window it has.</summary>
    public static void OpenProfile(string profile)
    {
#if WINDOWS
        var logger = MauiProgram.LoggerFactory.CreateLogger(typeof(AppWindows));
        if (WindowOf(profile) is { } open)
        {
            logger.LogInformation("Windows: {Profile} has a window; bringing it forward", profile);
            WindowFront.Bring(open, logger);
            return;
        }
        logger.LogInformation("Windows: opening {Profile} in its own window", profile);
        Application.Current!.OpenWindow(New(profile));
#else
        throw new PlatformNotSupportedException("Profile windows are the Windows app's");
#endif
    }

    /// <summary>
    /// The main window, at its saved place, and after it the profile windows the app had when it last closed (#341).
    /// Android and iOS have the one window, where it always is.
    /// </summary>
    public static Window Start()
    {
#if WINDOWS
        var main = New(profile: null, WindowPlaces.MainPlace());
        _ = WindowPlaces.RestoreProfilesAsync((profile, place) =>
        {
            if (WindowOf(profile) is null) Application.Current?.OpenWindow(New(profile, place));
        });
        return main;
#else
        return New(profile: null);
#endif
    }

#if WINDOWS
    /// <summary>The window locked to the profile, by name without case.</summary>
    private static Window? WindowOf(string profile) => Application.Current?.Windows
        .FirstOrDefault(w => w.Page is MainPage { Profile: { } p } && string.Equals(p, profile, StringComparison.OrdinalIgnoreCase));

    /// <summary>The main window: the one whose page no profile locks.</summary>
    private static Window? Main => Application.Current?.Windows.FirstOrDefault(w => w.Page is MainPage { Profile: null });

    /// <summary>
    /// A later start handed its arguments to this app (SingleInstance): <c>--profile Work</c> opens Work's window, or
    /// brings it forward; anything else brings the main window forward.
    /// </summary>
    public static void HandOff(IReadOnlyList<string> args, ILogger logger)
    {
        var at = args.ToList().FindIndex(a => string.Equals(a, ProfileArgument, StringComparison.OrdinalIgnoreCase));
        if (at >= 0 && at + 1 < args.Count && !string.IsNullOrWhiteSpace(args[at + 1]))
        {
            OpenProfile(args[at + 1].Trim());
            return;
        }
        if (Main is { } main) WindowFront.Bring(main, logger);
        else logger.LogInformation("Windows: no window yet to bring to the front");
    }
#endif
}
