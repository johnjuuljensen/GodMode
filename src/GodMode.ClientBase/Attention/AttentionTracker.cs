using GodMode.Shared.Models;

namespace GodMode.ClientBase.Attention;

/// <summary>
/// Turns each server's whole attention list (<c>GetAttention</c>, <c>AttentionChanged</c>) into the
/// notifications to show and to cancel: an item is shown when it is new or changed what it needs,
/// and cancelled when its server no longer lists it, whoever answered it. It starts from what the notifier
/// shows already (<see cref="IAttentionNotifier.Showing"/>), so what an earlier run left is kept right too.
/// </summary>
public sealed class AttentionTracker
{
    private readonly Lock _lock = new();
    private readonly IAttentionNotifier _notifier;
    /// <summary>By server, then project. A null item is one shown before this tracker, not yet listed since.</summary>
    private readonly Dictionary<string, Dictionary<string, AttentionItem?>> _byServer = [];

    public AttentionTracker(IAttentionNotifier notifier)
    {
        _notifier = notifier;
        foreach (var link in notifier.Showing())
        {
            if (!_byServer.TryGetValue(link.ServerId, out var items))
                _byServer[link.ServerId] = items = [];
            items[link.ProjectId] = null;
        }
    }

    /// <summary>The server's list is now <paramref name="items"/>.</summary>
    public void Update(string serverId, string serverName, IReadOnlyCollection<AttentionItem> items)
    {
        lock (_lock)
        {
            var shown = _byServer.GetValueOrDefault(serverId) ?? [];
            // A server lists a project once; should it list one twice, the last wins, as in the inbox
            var fresh = new Dictionary<string, AttentionItem?>();
            foreach (var item in items)
                fresh[item.ProjectId] = item;

            foreach (var projectId in shown.Keys.Where(id => !fresh.ContainsKey(id)))
                _notifier.Cancel(new AttentionLink(serverId, projectId));
            foreach (var item in fresh.Values.OfType<AttentionItem>().Where(i => !shown.TryGetValue(i.ProjectId, out var was) || Changed(was, i)))
                _notifier.Show(new AttentionNotice(new AttentionLink(serverId, item.ProjectId), serverName, item));

            if (fresh.Count > 0) _byServer[serverId] = fresh;
            else _byServer.Remove(serverId);
        }
    }

    /// <summary>The server is gone: cancels everything shown for it.</summary>
    public void Remove(string serverId) => Update(serverId, "", []);

    /// <summary>Cancels everything shown for servers other than these (unregistered since an earlier run showed it).</summary>
    public void RemoveAllExcept(IEnumerable<string> serverIds)
    {
        List<string> gone;
        lock (_lock) gone = [.. _byServer.Keys.Except(serverIds)];
        foreach (var serverId in gone)
            Remove(serverId);
    }

    /// <summary>Same project, but what it needs is different (a new question, another tool call), or not known (shown before).</summary>
    private static bool Changed(AttentionItem? was, AttentionItem now) =>
        was is null || was.Kind != now.Kind || was.Since != now.Since || was.Text != now.Text;
}
