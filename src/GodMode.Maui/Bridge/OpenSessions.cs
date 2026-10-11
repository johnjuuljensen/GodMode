using System.Collections.Concurrent;
using GodMode.ClientBase.Attention;

namespace GodMode.Maui.Bridge;

/// <summary>
/// The session each window's main view shows (#549), as its page says (session.open): by window, its profile ("" for
/// the main window). A notifier holds back the notification of a session shown here while the app is in front: the
/// user is looking at it. Android has the one window; Windows' toasts (#491) can ask by window.
/// </summary>
public static class OpenSessions
{
    private static readonly ConcurrentDictionary<string, AttentionLink> ByWindow = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The window's main view shows <paramref name="session"/>, or none.</summary>
    public static void Set(string window, AttentionLink? session)
    {
        if (session is null) ByWindow.TryRemove(window, out _);
        else ByWindow[window] = session;
    }

    /// <summary>Whether a window's main view shows the session.</summary>
    public static bool IsOpen(AttentionLink session) => ByWindow.Values.Contains(session);
}
