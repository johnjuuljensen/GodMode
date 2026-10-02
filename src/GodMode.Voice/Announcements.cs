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
    private HeardTwoWays? _unsure;

    public ProjectRef? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    /// <summary>
    /// The final the chat node answers now, while the recognizer heard it more than one way (VoiceBot#61); null while
    /// it was heard one way. Set by <see cref="HeardNode"/> for the chat's evaluation: the tools that act do nothing on
    /// it, and the ones that read leave <see cref="Current"/> as it is (<see cref="VoiceTools"/>).
    /// </summary>
    public HeardTwoWays? Unsure
    {
        get => Volatile.Read(ref _unsure);
        set => Volatile.Write(ref _unsure, value);
    }

    private ReplyReading? _reading;

    /// <summary>
    /// The reply being read in parts (<see cref="VoiceTools.ReadReplyAsync"/>), and the part to read next: what
    /// "læs videre" reads on from (<see cref="VoiceTools.ReadMoreText"/>). Null before any was read; a new read replaces it.
    /// </summary>
    public ReplyReading? Reading
    {
        get => Volatile.Read(ref _reading);
        set => Volatile.Write(ref _reading, value);
    }

    private ConcurrentQueue<string> _sent = new();

    /// <summary>An answer reached <paramref name="handle"/>: <see cref="VoiceTools.AnswerAsync"/>, once the server took it.</summary>
    public void Sent(string handle) => Volatile.Read(ref _sent).Enqueue(handle);

    /// <summary>
    /// The handles answers were sent to since the last take, and none from now on: <see cref="SentNode"/> takes them
    /// before the chat's evaluation and after it, so what it holds after is this turn's sends alone.
    /// </summary>
    public IReadOnlyList<string> TakeSent() => [.. Interlocked.Exchange(ref _sent, new())];
}

/// <summary>A project's reply in the parts voice reads it in, and the index of the part to read next (its count once all were read).</summary>
public sealed record ReplyReading(ProjectRef Project, string Handle, IReadOnlyList<string> Parts, int Next);

/// <summary>A final and the earlier readings of the same utterance it revised: partials it contradicts, not only extends.</summary>
public sealed record HeardTwoWays(string Final, IReadOnlyList<string> Readings);

/// <summary>
/// GodMode's wording of the announcements queued up to a pause: one as it is, several after "3 venter på dig:". The
/// project it names is what the conversation is about next (<see cref="VoiceConversation"/>), none when it names several.
/// An announcement of no project (a create's outcome) leaves the conversation as it is, said alone.
/// </summary>
public sealed class GodModeAnnouncementFormatter(VoicePhrases phrases, VoiceConversation conversation) : IAnnouncementFormatter
{
    public string Format(IReadOnlyList<Announcement> announcements, SessionLanguages languages)
    {
        string[] texts = [.. announcements.Select(a => a.Text.Trim().TrimEnd('.')).Where(t => t.Length > 0)];
        var projects = announcements.Select(a => a.Source).Distinct().ToList();
        if (projects is not [null])
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
            return string.Join(" ", announcements.Select(a => a.Text.Trim()).Where(t => t.Length > 0)
                .Select(t => t[^1] is '.' or '!' or '?' ? t : t + "."));
        }
    }
}

/// <summary>
/// The attention lists of every server as last heard. Each item is announced once, by its project's handle: when it
/// first appears (at the start, everything waiting then), and not again when a connection is made again. An item is
/// the same while its project needs the same thing since the same time. Handles are the <see cref="ProjectBoard"/>'s
/// to give: an item of a project it has not heard of (yet, or any more) is announced once it has, and never before.
/// </summary>
public sealed class AttentionBoard
{
    private readonly ProjectHandles _handles;
    private readonly ConcurrentDictionary<string, IReadOnlyList<ServerAttentionItem>> _lists = new();
    private readonly ConcurrentDictionary<string, byte> _announced = new();
    private readonly Lock _lock = new();
    private Action<ServerAttentionItem, string>? _announce;
    private readonly List<ServerAttentionItem> _unannounced = [];

    public AttentionBoard(IGodModeServers servers, ProjectHandles handles, ProjectBoard projects)
    {
        _handles = handles;
        servers.AttentionChanged += (serverId, serverName, items) =>
            Update(serverId, [.. items.Select(i => new ServerAttentionItem(serverId, serverName, i))]);
        // A project heard of now may have an item that waited for its handle
        projects.Changed += () =>
        {
            foreach (var list in _lists.Values) Announce(list);
        };
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
        {
            if (_handles.Of(item.Project) is { } handle)
                announce(item, handle);
        }
    }

    private void Update(string serverId, IReadOnlyList<ServerAttentionItem> items)
    {
        _lists[serverId] = items;
        Announce(items);
    }

    private void Announce(IReadOnlyList<ServerAttentionItem> items)
    {
        foreach (var item in items)
        {
            if (_handles.Of(item.Project) is not { } handle) continue;
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
