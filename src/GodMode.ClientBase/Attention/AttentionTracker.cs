using GodMode.Shared.Models;

namespace GodMode.ClientBase.Attention;

/// <summary>
/// Turns each server's whole attention list (<c>GetAttention</c>, <c>AttentionChanged</c>) into the
/// notifications to show and to cancel: an item is shown when it is new or changed what it needs,
/// and cancelled when its server no longer lists it, whoever answered it.
/// </summary>
public sealed class AttentionTracker(IAttentionNotifier notifier)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Dictionary<string, AttentionItem>> _byServer = [];

    /// <summary>The server's list is now <paramref name="items"/>.</summary>
    public void Update(string serverId, string serverName, IReadOnlyCollection<AttentionItem> items)
    {
        lock (_lock)
        {
            var shown = _byServer.GetValueOrDefault(serverId) ?? [];
            // A server lists a project once; should it list one twice, the last wins, as in the inbox
            var fresh = new Dictionary<string, AttentionItem>();
            foreach (var item in items)
                fresh[item.ProjectId] = item;

            foreach (var projectId in shown.Keys.Where(id => !fresh.ContainsKey(id)))
                notifier.Cancel(new AttentionLink(serverId, projectId));
            foreach (var item in fresh.Values.Where(i => !shown.TryGetValue(i.ProjectId, out var was) || Changed(was, i)))
                notifier.Show(new AttentionNotice(new AttentionLink(serverId, item.ProjectId), serverName, item));

            if (fresh.Count > 0) _byServer[serverId] = fresh;
            else _byServer.Remove(serverId);
        }
    }

    /// <summary>The server is gone: cancels everything shown for it.</summary>
    public void Remove(string serverId) => Update(serverId, "", []);

    /// <summary>Same project, but what it needs is different (a new question, another tool call).</summary>
    private static bool Changed(AttentionItem was, AttentionItem now) =>
        was.Kind != now.Kind || was.Since != now.Since || was.Text != now.Text;
}
