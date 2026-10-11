using GodMode.Shared.Enums;

namespace GodMode.ClientBase.Attention;

/// <summary>
/// How a Windows toast shows an attention item (#491): as a banner on screen, or straight into the notification centre;
/// with the toast's sound, or silent.
/// </summary>
public sealed record AttentionToast(bool Banner, bool Sound)
{
    /// <summary>
    /// Whether an item is toasted, and how, apart from the toast itself:
    /// <list type="bullet">
    /// <item>One for the inbox alone (<see cref="AttentionAlert.Inbox"/>) is not.</item>
    /// <item>Nor one whose session a window shows while that window is the one in front (#549): the user is looking at
    /// it. One open in a window behind another, or minimised, is.</item>
    /// <item>One that interrupts (<see cref="AttentionAlert.Interrupt"/>) is a banner, with the sound unless this device's
    /// sound is off (#438). One still showing from an earlier run of the app goes quietly into the centre: it alerted then.</item>
    /// <item>One that notifies (<see cref="AttentionAlert.Notify"/>) goes into the centre, silent, as Android's notify channel
    /// goes into the shade, and only when this device toasts what notifies: off by default, the desktop keeps those in the inbox.</item>
    /// </list>
    /// </summary>
    /// <param name="alert">The item's alert, its session's tier.</param>
    /// <param name="toastNotify">Whether this device toasts what notifies.</param>
    /// <param name="sound">Whether this device makes a sound for what interrupts.</param>
    /// <param name="windowsShowing">The windows (their profile, "" for the main window) whose main view shows the item's session.</param>
    /// <param name="foreground">The window in front, if it is one of the app's; null when another app's is.</param>
    /// <param name="showingFromBefore">Its toast is in the centre from an earlier run, and this run has not toasted it yet.</param>
    /// <returns>Null when the item is not toasted, and what it shows under its key is removed.</returns>
    public static AttentionToast? For(AttentionAlert alert, bool toastNotify, bool sound,
        IReadOnlyCollection<string> windowsShowing, string? foreground, bool showingFromBefore)
    {
        if (foreground is not null && windowsShowing.Contains(foreground, StringComparer.OrdinalIgnoreCase)) return null;
        return alert switch
        {
            AttentionAlert.Interrupt when showingFromBefore => new AttentionToast(Banner: false, Sound: false),
            AttentionAlert.Interrupt => new AttentionToast(Banner: true, Sound: sound),
            AttentionAlert.Notify when toastNotify => new AttentionToast(Banner: false, Sound: false),
            _ => null,
        };
    }
}
