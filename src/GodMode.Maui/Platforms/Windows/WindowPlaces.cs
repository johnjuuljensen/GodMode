using System.Runtime.InteropServices;
using GodMode.ClientBase;
using GodMode.ClientBase.Layout;
using GodMode.ClientBase.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace GodMode.Maui;

/// <summary>
/// Where the app's windows are, kept in <c>~/.godmode/windows.json</c> (#341; a debug build keeps its own, #350), and put back on a restart: the main
/// window at once, the profile windows once the servers have said which profiles there are (<see cref="ProfileCensus"/>).
/// Each window's place is read every <see cref="PollInterval"/> and the file is written when any of it changed (a move,
/// a resize, maximising, another desktop), when a window opens or closes, and when the app closes.
/// </summary>
internal static class WindowPlaces
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

#if SINGLE_INSTANCE
    private const bool SingleInstanceBuild = true;
#else
    private const bool SingleInstanceBuild = false;
#endif

    /// <summary>windows.json, or windows.debug.json in a build that runs beside the installed app (#350).</summary>
    private static readonly string FilePath = WindowLayout.PathFor(GodModePaths.AppDataDirectory, SingleInstanceBuild);
    private static readonly ILogger Logger = MauiProgram.LoggerFactory.CreateLogger(typeof(WindowPlaces));

    /// <summary>The windows open now, the main window first, then in the order they opened. UI thread only.</summary>
    private static readonly List<Tracked> Open = [];

    /// <summary>The layout the app started with; null when there was none.</summary>
    private static WindowLayout? _started;

    /// <summary>Saved profile windows not reopened yet: saved as they were, so a save before they open keeps them.</summary>
    private static List<SavedWindow> _pending = [];

    /// <summary>The desktop the user was on when the app started.</summary>
    private static Guid? _startDesktop;

    private static bool _loaded, _quitting;
    private static string? _written;
    private static DispatcherQueueTimer? _poll;

    /// <summary>The main window's saved place on this start, placed on the screens there are now.</summary>
    public static WindowPlace? MainPlace() => Started()?.Main is { } main ? Place(main) : null;

    /// <summary>
    /// A window has its platform window, not shown yet: it is put at its place (if it has one), tracked, and saved from
    /// now on.
    /// </summary>
    public static void Opened(Microsoft.UI.Xaml.Window platform, string? profile, WindowPlace? place)
    {
        var tracked = new Tracked(platform, profile, place?.Bounds);
        if (place is not null)
        {
            try
            {
                PutAt(platform, place, tracked.Name);
            }
            catch (Exception ex) when (ex is COMException or ArgumentException)
            {
                Logger.LogWarning("Windows: could not put {Window} back where it was: {Error}", tracked.Name, ex.Message);
            }
        }
        if (profile is not null) _pending.RemoveAll(p => string.Equals(p.Profile, profile, StringComparison.OrdinalIgnoreCase));
        if (profile is null) Open.Insert(0, tracked);
        else Open.Add(tracked);
        StartPolling(platform.DispatcherQueue);
        Save();
    }

    /// <summary>
    /// Bounds and maximised state first, then the desktop. The move is made before the window is shown: moving the
    /// foreground window to another desktop takes the user there. When Windows refuses it then, it is made once the
    /// window is shown.
    /// </summary>
    private static void PutAt(Microsoft.UI.Xaml.Window platform, WindowPlace place, string name)
    {
        platform.AppWindow.MoveAndResize(new RectInt32(place.Bounds.X, place.Bounds.Y, place.Bounds.Width, place.Bounds.Height));
        if (place.Maximized && platform.AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        // "The current desktop" is the one the user started the app on: showing the main window on its own desktop
        // has taken the user there by the time the profile windows open
        if ((place.Desktop ?? _startDesktop) is not { } desktop) return;
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(platform);
        if (VirtualDesktops.MoveTo(handle, desktop, Logger))
        {
            Logger.LogInformation("Windows: {Window} is on desktop {Desktop}", name, desktop);
            return;
        }
        void Shown(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
        {
            platform.Activated -= Shown;
            if (VirtualDesktops.MoveTo(handle, desktop, Logger))
                Logger.LogInformation("Windows: {Window} is on desktop {Desktop}, once shown", name, desktop);
        }
        platform.Activated += Shown;
    }

    /// <summary>A window closed. A profile window closed alone leaves the layout; a window closing because the app does stays in it.</summary>
    public static void Closed(Microsoft.UI.Xaml.Window platform)
    {
        if (_quitting) return;
        Open.RemoveAll(t => t.Platform == platform);
        Save();
    }

    /// <summary>The app is closing (its main window closed): what is open now is saved, and stays saved.</summary>
    public static void Quitting()
    {
        if (_quitting) return;
        Save();
        _quitting = true;
        _poll?.Stop();
    }

    /// <summary>
    /// Reopens the profile windows the app had, once the servers have said which profiles there are, or
    /// <see cref="ProfileCensus.DefaultWait"/> is up. A profile no server has isn't reopened; one a silent server might
    /// have is.
    /// </summary>
    public static async Task RestoreProfilesAsync(Action<string, WindowPlace> open)
    {
        Started();
        if (_pending.Count == 0) return;

        var ui = DispatcherQueue.GetForCurrentThread();
        ProfileCensus census;
        try
        {
            census = await ProfileCensus.TakeAsync(MauiProgram.Services.GetRequiredService<IServerDirectory>(), MauiProgram.LoggerFactory);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Windows: could not ask the servers for their profiles ({Error}); every saved window is reopened", ex.Message);
            census = new ProfileCensus(new HashSet<string>(), Complete: false);
        }
        Logger.LogInformation("Windows: the servers have the profiles {Profiles} ({Census})",
            string.Join(", ", census.Names), census.Complete ? "every server answered" : "not every server answered");
        ui.TryEnqueue(() =>
        {
            foreach (var window in _pending.ToList())
            {
                if (_quitting) return;
                if (WindowPlacer.Reopens(window.Profile!, census))
                {
                    Logger.LogInformation("Windows: reopening {Profile}'s window", window.Profile);
                    open(window.Profile!, Place(window));
                }
                else
                {
                    Logger.LogInformation("Windows: no server has the profile {Profile} now; its window is not reopened", window.Profile);
                }
            }
            _pending = [];
            Save();
            // Once the windows it opened are shown
            ui.TryEnqueue(DispatcherQueuePriority.Low, Settle);
        });
    }

    /// <summary>
    /// Showing a window on another desktop takes the user there, so after the restore the user is brought back to the
    /// desktop they started the app on, to a window there (the main window first). With none there, to the main window.
    /// </summary>
    private static void Settle()
    {
        if (_quitting || Open.Count == 0) return;
        var there = _startDesktop is { } start ? Open.FirstOrDefault(t => t.Capture().Desktop == start) : null;
        var window = there ?? Open[0];
        Logger.LogInformation("Windows: bringing {Window} to the front, on {Desktop}", window.Name,
            there is null ? "its own desktop" : "the desktop the app started on");
        WindowFront.Bring(window.Platform, Logger);
    }

    private static WindowLayout? Started()
    {
        if (!_loaded)
        {
            _started = WindowLayout.Load(FilePath, Logger);
            _pending = [.. _started?.Profiles ?? []];
            _startDesktop = VirtualDesktops.Current();
            _loaded = true;
        }
        return _started;
    }

    private static WindowPlace Place(SavedWindow saved)
    {
        var place = WindowPlacer.Place(saved, VirtualDesktops.Exists, Screens());
        if (saved.Desktop is { } desktop && place.Desktop is null)
            Logger.LogInformation("Windows: desktop {Desktop} is gone; {Window} opens on the one the app started on", desktop, saved.Profile ?? "the main window");
        if (place.Bounds != saved.Bounds)
            Logger.LogInformation("Windows: no screen shows {Window} where it was; it comes back on the primary screen", saved.Profile ?? "the main window");
        return place;
    }

    /// <summary>The screens' work areas, the primary first.</summary>
    private static IReadOnlyList<WindowBounds> Screens()
    {
        var primary = DisplayArea.Primary.DisplayId.Value;
        // By index: CsWinRT's projection of FindAll's list fails to enumerate
        var all = DisplayArea.FindAll();
        return Enumerable.Range(0, all.Count)
            .Select(i => all[i])
            .OrderBy(d => d.DisplayId.Value == primary ? 0 : 1)
            .Select(d => new WindowBounds(d.WorkArea.X, d.WorkArea.Y, d.WorkArea.Width, d.WorkArea.Height))
            .ToArray();
    }

    private static void StartPolling(DispatcherQueue queue)
    {
        if (_poll is not null) return;
        _poll = queue.CreateTimer();
        _poll.Interval = PollInterval;
        _poll.Tick += (_, _) => Save();
        _poll.Start();
    }

    /// <summary>Reads every open window's place and writes the file when it differs from what was last written.</summary>
    private static void Save()
    {
        if (_quitting) return;
        var layout = new WindowLayout(Open.Select(t => t.Capture()).Concat(_pending).ToArray());
        var json = layout.ToJson();
        if (json == _written) return;
        try
        {
            layout.Save(FilePath);
            _written = json;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning("Windows: could not save {Path}: {Error}", FilePath, ex.Message);
        }
    }

    /// <summary>One open window, and its place as last read.</summary>
    private sealed class Tracked(Microsoft.UI.Xaml.Window platform, string? profile, WindowBounds? normal)
    {
        private WindowBounds? _normal = normal;
        private bool _maximized;
        private Guid? _desktop;

        public Microsoft.UI.Xaml.Window Platform { get; } = platform;
        public string Name => profile ?? "the main window";

        /// <summary>
        /// Its place now: its bounds when neither maximised nor minimised (a maximised window keeps the bounds it
        /// had before, to come back to), whether it is maximised, and its desktop. A window Windows no longer answers
        /// for keeps what was last read.
        /// </summary>
        public SavedWindow Capture()
        {
            try
            {
                var app = Platform.AppWindow;
                var state = (app.Presenter as OverlappedPresenter)?.State ?? OverlappedPresenterState.Restored;
                var bounds = new WindowBounds(app.Position.X, app.Position.Y, app.Size.Width, app.Size.Height);
                if (state == OverlappedPresenterState.Restored) _normal = bounds;
                if (state != OverlappedPresenterState.Minimized) _maximized = state == OverlappedPresenterState.Maximized;
                _normal ??= bounds;
                _desktop = VirtualDesktops.Of(WinRT.Interop.WindowNative.GetWindowHandle(Platform)) ?? _desktop;
            }
            catch (Exception ex) when (ex is COMException or ObjectDisposedException)
            {
                // Closing: what was read last stands
            }
            return new SavedWindow(profile, _normal ?? new WindowBounds(0, 0, 0, 0), _maximized, _desktop);
        }
    }
}
