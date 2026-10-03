using System.Runtime.InteropServices;
using GodMode.ClientBase.Attention;
using GodMode.ClientBase.Services;
using GodMode.Shared.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GodMode.Maui;

/// <summary>
/// What interrupts on Windows (issue #438): an <see cref="AttentionWatcher"/> over every registered server, for the app's
/// life, that plays Windows' own notification sound when an important session's item arrives
/// (<see cref="AttentionAlert.Interrupt"/>), unless this device's sound is off (<see cref="AttentionSound"/>). Held by
/// the app, not a window, so several windows make one sound. Shows nothing yet: toasts are #491.
/// </summary>
public static class WindowsAttention
{
    private static AttentionWatcher? _watcher;

    /// <summary>Starts watching the servers registered now.</summary>
    public static void Start(IServiceProvider services, ILoggerFactory loggerFactory)
    {
        _watcher = new AttentionWatcher(services.GetRequiredService<IServerDirectory>(),
            new SoundNotifier(loggerFactory.CreateLogger<SoundNotifier>()), loggerFactory);
        Refresh();
    }

    /// <summary>Picks up a change to the server list.</summary>
    public static void Refresh()
    {
        if (_watcher is { } watcher) _ = watcher.RefreshAsync();
    }

    /// <summary>Plays the sound for each new or changed item that interrupts; a sound is all it shows.</summary>
    private sealed class SoundNotifier(ILogger logger) : IAttentionNotifier
    {
        private const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndAlias = 0x00010000;

        public void Show(AttentionNotice notice)
        {
            if (notice.Item.Alert != AttentionAlert.Interrupt || !AttentionSound.Enabled) return;
            logger.LogInformation("{Key} interrupts: {Kind}", notice.Link.Key, notice.Item.Kind);
            // The user's own sound for a notification, from their Windows sound scheme: nothing to bundle
            if (!PlaySound("Notification.Default", IntPtr.Zero, SndAlias | SndAsync | SndNoDefault))
                logger.LogDebug("Windows played no notification sound for {Key}", notice.Link.Key);
        }

        public void Cancel(AttentionLink link) { }

        public IReadOnlyCollection<AttentionLink> Showing() => [];

        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "PlaySoundW")]
        private static extern bool PlaySound(string sound, IntPtr module, uint flags);
    }
}
