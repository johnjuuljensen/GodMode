using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using GodMode.ClientBase.Attention;
using GodMode.ClientBase.Services;
using GodMode.Maui.Bridge;
using GodMode.Shared.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace GodMode.Maui;

/// <summary>
/// What interrupts on Windows (#438, #491): an <see cref="AttentionWatcher"/> over every registered server, for the app's
/// life, that toasts each item <see cref="AttentionToast.For"/> says to, through WinAppSDK's
/// <see cref="AppNotificationManager"/>: what interrupts as a banner with Windows' notification sound (silent when this
/// device's sound is off, <see cref="AttentionSound"/>), what notifies silently into the notification centre when this
/// device toasts it (<see cref="ToastNotify"/>), and nothing for a session open in the window in front (#549). A toast is
/// removed when its server no longer lists the item. A click opens the item in the main window and brings it to the front,
/// whether the app runs (<see cref="AppNotificationManager.NotificationInvoked"/>) or Windows starts it for the click
/// (<see cref="AppInstance.GetActivatedEventArgs"/>). Held by the app, not a window, so several windows make one toast.
/// The app is unpackaged: it registers as a notifier under its own name and icon, which needs no package identity.
/// </summary>
public static class WindowsAttention
{
    private const string ToastNotifyKey = "attention.toast-notify";
    private const string LinkArgument = "link";

    private static AttentionWatcher? _watcher;
    private static ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>The app's windows by handle: their profile, "" for the main window.</summary>
    private static readonly ConcurrentDictionary<nint, string> Windows = new();

    /// <summary>
    /// Whether this device toasts what notifies (<see cref="AttentionAlert.Notify"/>), silently into the notification
    /// centre. Off by default: on the desktop those stay in the inbox. The app's settings change it (<c>attention.toasts.set</c>).
    /// </summary>
    public static bool ToastNotify
    {
        get => Preferences.Default.Get(ToastNotifyKey, false);
        set => Preferences.Default.Set(ToastNotifyKey, value);
    }

    /// <summary>Registers as a notifier, opens what a toast's click started the app for, and watches the servers registered now.</summary>
    public static void Start(IServiceProvider services, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger(typeof(WindowsAttention));
        var toasts = Toasts.Register(_logger);
        _watcher = new AttentionWatcher(services.GetRequiredService<IServerDirectory>(),
            new GuardedNotifier(new ToastNotifier(toasts, loggerFactory.CreateLogger<ToastNotifier>()), loggerFactory.CreateLogger<GuardedNotifier>()),
            loggerFactory);
        Refresh();
    }

    /// <summary>Picks up a change to the server list.</summary>
    public static void Refresh()
    {
        if (_watcher is { } watcher) _ = watcher.RefreshAsync();
    }

    /// <summary>The app is closing: a click from now on starts it again. The toasts stay, to open what they show then.</summary>
    public static void Stop()
    {
        try
        {
            if (Toasts.Registered) AppNotificationManager.Default.Unregister();
        }
        catch (COMException ex)
        {
            _logger.LogDebug("Toasts: could not unregister: {Error}", ex.Message);
        }
    }

    /// <summary>A window of the app has its platform window: its toasts are held back while it is in front and shows their session.</summary>
    public static void WindowOpened(Microsoft.UI.Xaml.Window platform, string? profile)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(platform);
        Windows[handle] = profile ?? "";
        platform.Closed += (_, _) => Windows.TryRemove(handle, out _);
    }

    /// <summary>The window in front, if it is one of the app's: its profile, "" for the main window.</summary>
    private static string? Foreground() => Windows.GetValueOrDefault(GetForegroundWindow());

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    /// <summary>A click on a toast: the item opens in the main window, which comes to the front.</summary>
    private static void Open(AttentionLink link)
    {
        _logger.LogInformation("Toast clicked: {Key}", link.Key);
        PendingAttentionLink.Set(link);
        MainThread.BeginInvokeOnMainThread(() => AppWindows.HandOff([], _logger));
    }

    /// <summary>The app's registration with Windows' notifications, which makes the toasts and takes their clicks.</summary>
    private static class Toasts
    {
        public static bool Registered { get; private set; }

        /// <summary>What Windows' notification settings list it as: each debug build (a worktree's) registers apart from the installed app.</summary>
#if DEBUG
        private const string DisplayName = "GodMode (debug)";
#else
        private const string DisplayName = "GodMode";
#endif

        /// <summary>Registers, and opens the item whose toast Windows started this process for. False when Windows shows no toasts for the app.</summary>
        public static bool Register(ILogger logger)
        {
            try
            {
                if (!AppNotificationManager.IsSupported())
                {
                    logger.LogInformation("Toasts: Windows does not support app notifications here; what interrupts makes a sound alone");
                    return false;
                }
                var manager = AppNotificationManager.Default;
                // Before Register, so a click is never missed
                manager.NotificationInvoked += (_, args) =>
                {
                    if (LinkOf(args) is { } link) Open(link);
                };
                // Unpackaged, the app names itself; a package would take its name and icon from its manifest
                manager.Register(DisplayName, new Uri(Path.Combine(AppContext.BaseDirectory, "appiconLogo.scale-200.png")));
                Registered = true;
                if (AppInstance.GetCurrent().GetActivatedEventArgs() is { Kind: ExtendedActivationKind.AppNotification, Data: AppNotificationActivatedEventArgs started }
                    && LinkOf(started) is { } startedFor)
                {
                    logger.LogInformation("Toasts: started by a click on {Key}", startedFor.Key);
                    PendingAttentionLink.Set(startedFor);
                }
                logger.LogInformation("Toasts: registered; Windows shows them: {Setting}", manager.Setting);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Toasts: could not register, so what interrupts makes a sound alone: {Error}", ex.Message);
                return false;
            }
        }

        private static AttentionLink? LinkOf(AppNotificationActivatedEventArgs args) =>
            args.Arguments.TryGetValue(LinkArgument, out var key) ? AttentionLink.FromKey(key) : null;
    }

    /// <summary>One toast per item, tagged by a hash of its key (a tag holds 64 characters), with the key in its arguments.</summary>
    private sealed class ToastNotifier(bool toasts, ILogger logger) : IAttentionNotifier
    {
        private const string Group = "godmode.attention";
        private const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndAlias = 0x00010000;

        /// <summary>Keys this run toasted; others in the centre are from an earlier run.</summary>
        private readonly ConcurrentDictionary<string, byte> _toasted = new();
        /// <summary>Keys an earlier run left in the centre, not toasted again yet.</summary>
        private readonly ConcurrentDictionary<string, byte> _fromBefore = new();

        public void Show(AttentionNotice notice)
        {
            var key = notice.Link.Key;
            var item = notice.Item;
            var toast = AttentionToast.For(item.Alert, ToastNotify, AttentionSound.Enabled,
                OpenSessions.WindowsShowing(notice.Link), Foreground(), _fromBefore.ContainsKey(key));
            if (toast is null)
            {
                logger.LogDebug("Toast {Key}: {Kind}, {Alert}, not toasted", key, item.Kind, item.Alert);
                if (_toasted.ContainsKey(key) || _fromBefore.ContainsKey(key)) Cancel(notice.Link);
                return;
            }
            if (!toasts)
            {
                // No toasts (Windows lacks them, or refused the registration): what interrupts still sounds
                if (toast is { Banner: true, Sound: true } && !PlaySound("Notification.Default", IntPtr.Zero, SndAlias | SndAsync | SndNoDefault))
                    logger.LogDebug("Windows played no notification sound for {Key}", key);
                return;
            }

            // A question says it is one, then what it asks and its options (#454); what does not fit, a click opens
            var builder = new AppNotificationBuilder()
                .AddArgument(LinkArgument, key)
                .AddText(AttentionNotificationText.Title(item))
                .AddText(AttentionNotificationText.Line(item));
            if (AttentionNotificationText.Details(item) is { } details) builder.AddText(details);
            builder.SetAttributionText(notice.ServerName)
                .SetTimeStamp(new DateTimeOffset(DateTime.SpecifyKind(item.Since, DateTimeKind.Utc)));
            if (!toast.Sound) builder.MuteAudio();
            // The builder refuses a payload over about 5 KB (an ArgumentException), and Windows may refuse to show one: either
            // costs this toast alone, never the tracker's next item or removal
            try
            {
                var notification = builder.BuildNotification();
                notification.Tag = Tag(key);
                notification.Group = Group;
                // Not a banner: straight into the notification centre
                notification.SuppressDisplay = !toast.Banner;
                notification.Priority = toast.Banner ? AppNotificationPriority.High : AppNotificationPriority.Default;
                AppNotificationManager.Default.Show(notification);
                _toasted[key] = 0;
                _fromBefore.TryRemove(key, out _);
                logger.LogInformation("Toast {Key}: {Kind}, {How}", key, item.Kind,
                    toast.Banner ? toast.Sound ? "a banner with sound" : "a silent banner" : "silently in the centre");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Toast {Key}: not shown", key);
            }
        }

        public void Cancel(AttentionLink link)
        {
            _toasted.TryRemove(link.Key, out _);
            _fromBefore.TryRemove(link.Key, out _);
            if (!toasts) return;
            logger.LogDebug("Toast {Key} removed", link.Key);
            _ = Remove(link.Key);
        }

        private async Task Remove(string key)
        {
            try
            {
                await AppNotificationManager.Default.RemoveByTagAndGroupAsync(Tag(key), Group);
            }
            catch (COMException ex)
            {
                logger.LogDebug("Toast {Key}: could not remove it: {Error}", key, ex.Message);
            }
        }

        /// <summary>The items an earlier run left in the notification centre: the tracker removes them once their servers stop listing them.</summary>
        public IReadOnlyCollection<AttentionLink> Showing()
        {
            if (!toasts) return [];
            try
            {
                var left = AppNotificationManager.Default.GetAllAsync().AsTask().GetAwaiter().GetResult()
                    .Where(n => n.Group == Group)
                    .Select(n => LinkInPayload(n.Payload))
                    .OfType<AttentionLink>()
                    .ToList();
                foreach (var link in left) _fromBefore[link.Key] = 0;
                logger.LogInformation("Toasts: {Count} left in the notification centre by an earlier run", left.Count);
                return left;
            }
            catch (COMException ex)
            {
                logger.LogDebug("Toasts: could not list what shows: {Error}", ex.Message);
                return [];
            }
        }

        /// <summary>
        /// The link in a toast's launch arguments (<c>link={key}</c>), as the builder escapes them: '%', ';' and '=' as
        /// %XX, which a key's own escapes survive (each '%' of them became %25).
        /// </summary>
        private static AttentionLink? LinkInPayload(string payload)
        {
            try
            {
                var launch = XDocument.Parse(payload).Root?.Attribute("launch")?.Value ?? "";
                return launch.Split(';').Select(a => a.Split('=', 2)).FirstOrDefault(a => a is [LinkArgument, _]) is [_, var value]
                    ? AttentionLink.FromKey(Uri.UnescapeDataString(value))
                    : null;
            }
            catch (System.Xml.XmlException)
            {
                return null;
            }
        }

        private static string Tag(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];

        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "PlaySoundW")]
        private static extern bool PlaySound(string sound, IntPtr module, uint flags);
    }
}
