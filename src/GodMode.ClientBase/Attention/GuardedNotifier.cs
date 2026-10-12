using Microsoft.Extensions.Logging;

namespace GodMode.ClientBase.Attention;

/// <summary>
/// An <see cref="IAttentionNotifier"/> whose failures cost the one call: what the inner notifier throws is logged, not
/// passed to the <see cref="AttentionTracker"/>, which would otherwise fail the server's connection (on connect) or never
/// record its new list (on AttentionChanged), so that no later item would show and nothing would be removed. Windows'
/// toasts (#491) go through it: a payload the toast builder refuses, say.
/// </summary>
public sealed class GuardedNotifier(IAttentionNotifier inner, ILogger logger) : IAttentionNotifier
{
    public void Show(AttentionNotice notice)
    {
        try { inner.Show(notice); }
        catch (Exception ex) { logger.LogWarning(ex, "Notification {Key}: could not show it", notice.Link.Key); }
    }

    public void Cancel(AttentionLink link)
    {
        try { inner.Cancel(link); }
        catch (Exception ex) { logger.LogWarning(ex, "Notification {Key}: could not remove it", link.Key); }
    }

    public IReadOnlyCollection<AttentionLink> Showing()
    {
        try { return inner.Showing(); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notifications: could not list what shows");
            return [];
        }
    }
}
