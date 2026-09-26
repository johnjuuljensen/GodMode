using System.Collections.Concurrent;
using GodMode.Shared.Models;
using Microsoft.Extensions.Logging;
using VoiceBot.Core.Pipeline;
using VoiceBot.Core.Resources;

namespace GodMode.Voice;

/// <summary>
/// What the conversation is about: the project an answer goes to when the user names none. It is the project last
/// announced alone, read out, or answered. An announcement of several projects at once leaves it unset, so a reply
/// then must name one.
/// </summary>
public sealed class VoiceConversation
{
    private ProjectRef? _current;

    public ProjectRef? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}

/// <summary>
/// GodMode's wording of the announcements queued up to a pause: one as it is, several after "3 venter på dig:". What
/// it says is what the conversation is about next (<see cref="VoiceConversation"/>).
/// </summary>
public sealed class GodModeAnnouncementFormatter(VoicePhrases phrases, VoiceConversation conversation) : IAnnouncementFormatter
{
    public string Format(IReadOnlyList<Announcement> announcements, SessionLanguages languages)
    {
        string[] texts = [.. announcements.Select(a => a.Text.Trim().TrimEnd('.')).Where(t => t.Length > 0)];
        var projects = announcements.Select(a => a.Source).Distinct().ToList();
        conversation.Current = projects is [var only] ? ProjectRef.FromKey(only) : null;

        return texts switch
        {
            [] => "",
            [var one] => one + ".",
            _ => $"{phrases.Several(texts.Length)} {string.Join(". ", texts)}.",
        };
    }
}

/// <summary>
/// A formatter that never throws: VoiceBot's session stops announcing for good when its formatter throws once
/// (johnjuuljensen/VoiceBot#27). A failure is logged, and the announcements are said plainly, one after the other.
/// </summary>
public sealed class NeverThrowingFormatter(IAnnouncementFormatter inner, ILogger logger) : IAnnouncementFormatter
{
    public string Format(IReadOnlyList<Announcement> announcements, SessionLanguages languages)
    {
        try
        {
            return inner.Format(announcements, languages);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Voice: the announcement formatter failed on {Count} announcement(s); saying them plainly", announcements.Count);
            return string.Join(" ", announcements.Select(a => a.Text.Trim()).Where(t => t.Length > 0));
        }
    }
}

/// <summary>
/// The attention lists of every server as last heard, with a handle for each project in them. Each item is announced
/// once: when it first appears (at the start, everything waiting then), and not again when a connection is made again.
/// An item is the same while its project needs the same thing since the same time.
/// </summary>
public sealed class AttentionBoard
{
    private readonly ProjectHandles _handles;
    private readonly ConcurrentDictionary<string, IReadOnlyList<ServerAttentionItem>> _lists = new();
    private readonly ConcurrentDictionary<string, byte> _announced = new();
    private readonly Lock _lock = new();
    private Action<ServerAttentionItem, string>? _announce;
    private readonly List<ServerAttentionItem> _unannounced = [];

    public AttentionBoard(IGodModeServers servers, ProjectHandles handles)
    {
        _handles = handles;
        servers.AttentionChanged += (serverId, serverName, items) =>
            Update(serverId, [.. items.Select(i => new ServerAttentionItem(serverId, serverName, i))]);
    }

    /// <summary>Every server's items, as last heard.</summary>
    public IReadOnlyList<ServerAttentionItem> Items => [.. _lists.Values.SelectMany(l => l).OrderBy(i => i.Item.Since)];

    /// <summary>The project's item, if it needs the user.</summary>
    public ServerAttentionItem? ItemOf(ProjectRef project) =>
        _lists.TryGetValue(project.ServerId, out var list) ? list.FirstOrDefault(i => i.Item.ProjectId == project.ProjectId) : null;

    /// <summary>
    /// From now on, each new item goes to <paramref name="announce"/> with its project's handle; those that came before
    /// go at once.
    /// </summary>
    public void Attach(Action<ServerAttentionItem, string> announce)
    {
        List<ServerAttentionItem> waiting;
        lock (_lock)
        {
            _announce = announce;
            waiting = [.. _unannounced];
            _unannounced.Clear();
        }
        foreach (var item in waiting)
            announce(item, _handles.For(item.Project, item.Item.ProjectName));
    }

    private void Update(string serverId, IReadOnlyList<ServerAttentionItem> items)
    {
        _lists[serverId] = items;
        foreach (var item in items)
        {
            var handle = _handles.For(item.Project, item.Item.ProjectName);
            if (!_announced.TryAdd(Key(item), 0)) continue;

            Action<ServerAttentionItem, string>? announce;
            lock (_lock)
            {
                announce = _announce;
                if (announce is null) _unannounced.Add(item);
            }
            announce?.Invoke(item, handle);
        }
    }

    private static string Key(ServerAttentionItem item) =>
        $"{item.ServerId}\n{item.Item.ProjectId}\n{item.Item.Kind}\n{item.Item.Since:O}";
}
