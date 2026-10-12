using System.Collections.Concurrent;
using GodMode.ClientBase.Attention;

namespace GodMode.Maui.Bridge;

/// <summary>
/// The session each window's main view shows (#549), as its page says (session.open): by window, its profile ("" for
/// the main window). A notifier holds back the notification of a session shown here while the app is in front: the
/// user is looking at it. Android has the one window (<see cref="IsOpen"/>); Windows' toasts (#491) ask which windows
/// show it (<see cref="WindowsShowing"/>), and hold back only while one of them is the window in front.
/// Voice, shared by the windows, takes the one shown in the window that had focus last (#559, <see cref="Selected"/>).
/// </summary>
public static class OpenSessions
{
    private static readonly ConcurrentDictionary<string, AttentionLink> ByWindow = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock FocusLock = new();
    private static string _focused = "";

    /// <summary>
    /// <see cref="Selected"/> may have changed: a window's page said what it shows, or another window got the focus. Raised
    /// on whatever thread that came on; a listener takes the same session again as no change.
    /// </summary>
    public static event Action<AttentionLink?>? SelectionChanged;

    /// <summary>
    /// The session shown in the window that had focus last (#559): the user's selection, which voice talks about; null
    /// when that window shows none. The main window's until a window reports its focus (Android's one window).
    /// </summary>
    public static AttentionLink? Selected
    {
        get { lock (FocusLock) return ByWindow.GetValueOrDefault(_focused); }
    }

    /// <summary>The window's main view shows <paramref name="session"/>, or none.</summary>
    public static void Set(string window, AttentionLink? session)
    {
        if (session is null) ByWindow.TryRemove(window, out _);
        else ByWindow[window] = session;
        SelectionChanged?.Invoke(Selected);
    }

    /// <summary>The window (its profile, "" for the main window) got the focus: its session is the selection from now on.</summary>
    public static void Focused(string window)
    {
        lock (FocusLock) _focused = window;
        SelectionChanged?.Invoke(Selected);
    }

    /// <summary>The window closed: the main window's session is the selection again, if the focus was the closed one's.</summary>
    public static void Closed(string window)
    {
        lock (FocusLock)
        {
            if (!string.Equals(_focused, window, StringComparison.OrdinalIgnoreCase)) return;
            _focused = "";
        }
        SelectionChanged?.Invoke(Selected);
    }

    /// <summary>Whether a window's main view shows the session.</summary>
    public static bool IsOpen(AttentionLink session) => ByWindow.Values.Contains(session);

    /// <summary>The windows (profile, "" for the main window) whose main view shows the session.</summary>
    public static IReadOnlyCollection<string> WindowsShowing(AttentionLink session) =>
        ByWindow.Where(w => w.Value == session).Select(w => w.Key).ToArray();
}
