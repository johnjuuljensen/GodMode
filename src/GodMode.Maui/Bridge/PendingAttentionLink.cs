using GodMode.ClientBase.Attention;

namespace GodMode.Maui.Bridge;

/// <summary>
/// The attention item a notification tap opened, until React takes it (attention.take). A tap can come
/// before React has loaded (the app was not running), so it waits here, and <see cref="Arrived"/> tells a
/// loaded React to come for it.
/// </summary>
public static class PendingAttentionLink
{
    private static AttentionLink? _pending;

    /// <summary>A link is waiting.</summary>
    public static event Action? Arrived;

    /// <summary>How many handlers <see cref="Arrived"/> has: one per attached shell bridge.</summary>
    public static int Listening => Arrived?.GetInvocationList().Length ?? 0;

    public static void Set(AttentionLink link)
    {
        Interlocked.Exchange(ref _pending, link);
        Arrived?.Invoke();
    }

    /// <summary>The waiting link, once: null after it has been taken.</summary>
    public static AttentionLink? Take() => Interlocked.Exchange(ref _pending, null);
}
