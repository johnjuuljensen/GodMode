using System.Collections.Concurrent;
using GodMode.Shared.Enums;
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

    private string? _lastProfile;

    /// <summary>
    /// The profile spoken of last (<see cref="ProjectNames"/>, #450): a project in another is named with its profile.
    /// Null before any, and after a list of several profiles.
    /// </summary>
    public string? LastProfile
    {
        get => Volatile.Read(ref _lastProfile);
        set => Volatile.Write(ref _lastProfile, value);
    }

    private ReplyReading? _reading;

    /// <summary>
    /// The reply being read in parts (<see cref="VoiceTools.ReadReplyAsync"/>), and the part to read next: what
    /// "læs videre" reads on from (<see cref="VoiceTools.ReadMoreAsync"/>). Null before any was read; a new read replaces
    /// it, and a new reply of its project drops it.
    /// </summary>
    public ReplyReading? Reading
    {
        get => Volatile.Read(ref _reading);
        set => Volatile.Write(ref _reading, value);
    }

    private ConcurrentQueue<SpokenName> _sent = new();

    /// <summary>An answer reached the project named so: <see cref="VoiceTools.AnswerAsync"/>, once the server took it.</summary>
    public void Sent(SpokenName name) => Volatile.Read(ref _sent).Enqueue(name);

    /// <summary>
    /// The projects answers were sent to since the last take, and none from now on: <see cref="SentNode"/> takes them
    /// before the chat's evaluation and after it, so what it holds after is this turn's sends alone.
    /// </summary>
    public IReadOnlyList<SpokenName> TakeSent() => [.. Interlocked.Exchange(ref _sent, new())];

    private ConcurrentQueue<(SpokenName Name, AttentionItem Item)> _spoken = new();

    /// <summary>
    /// A tool read out the item of the project named so, which carries the session's own spoken reply
    /// (<see cref="AttentionItem.Spoken"/>): <see cref="SpokenNode"/> says it in place of the model's reply.
    /// </summary>
    public void Spoke(SpokenName name, AttentionItem item) => Volatile.Read(ref _spoken).Enqueue((name, item));

    /// <summary>The spoken replies read since the last take, oldest first, and none from now on: as <see cref="TakeSent"/>.</summary>
    public IReadOnlyList<(SpokenName Name, AttentionItem Item)> TakeSpoken() => [.. Interlocked.Exchange(ref _spoken, new())];

    private ConcurrentQueue<string> _read = new();

    /// <summary>
    /// A tool read out <paramref name="text"/>, a project's own words among it (its reply, question or result), for the
    /// model to say: <see cref="SentNode"/> does not take a reply that repeats it for a claim of a send (#411).
    /// </summary>
    public void ReadOut(string text) => Volatile.Read(ref _read).Enqueue(text);

    /// <summary>The texts read out since the last take, and none from now on: as <see cref="TakeSent"/>.</summary>
    public IReadOnlyList<string> TakeReadOut() => [.. Interlocked.Exchange(ref _read, new())];
}

/// <summary>
/// A project's reply in the parts voice reads it in, and the index of the part to read next (its count once all were
/// read). <paramref name="Replies"/> are the replies it was read from, the last <paramref name="Turns"/>: while the
/// project's are still these, the parts are what it said last.
/// </summary>
public sealed record ReplyReading(ProjectRef Project, string Handle, IReadOnlyList<string> Parts, int Next, int Turns, IReadOnlyList<AssistantReply> Replies);

/// <summary>
/// GodMode's wording of the announcements queued up to a pause: one as it is, several after "3 venter på dig:". The
/// project it names is what the conversation is about next (<see cref="VoiceConversation"/>), none when it names several.
/// An announcement of no project (a create's outcome) leaves the conversation as it is, said alone.
/// </summary>
public sealed class GodModeAnnouncementFormatter(VoicePhrases phrases, VoiceConversation conversation, AttentionBoard? board = null)
    : IAnnouncementFormatter
{
    public string Format(IReadOnlyList<Announcement> announcements, SessionLanguages languages)
    {
        // An important project's are said first (issue #438), the rest in the order they came
        string[] texts = [.. announcements.OrderByDescending(Interrupts).Select(a => Sentence(a.Text)).Where(t => t.Length > 0)];
        var projects = announcements.Select(a => a.Source).Distinct().ToList();
        if (projects is not [null])
            conversation.Current = projects is [var only] ? ProjectRef.FromKey(only) : null;

        return texts switch
        {
            [] => "",
            [var one] => one,
            _ => $"{phrases.Several(texts.Length)} {string.Join(" ", texts)}",
        };
    }

    /// <summary>Whether the announcement is of an item that interrupts: an important project's.</summary>
    private bool Interrupts(Announcement announcement) =>
        board != null && ProjectRef.FromKey(announcement.Source) is { } project
        && board.ItemOf(project)?.Item.Alert == AttentionAlert.Interrupt;

    /// <summary>The text as a sentence: ended with its own '?' or '!' (a session's spoken reply has them), else a '.'.</summary>
    internal static string Sentence(string text) =>
        text.Trim().TrimEnd('.') is { Length: > 0 } t ? t[^1] is '?' or '!' ? t : t + "." : "";
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
            // In the inbox alone (a quiet session's result or error, issue #438): listed when asked, never announced
            if (item.Item.Alert == AttentionAlert.Inbox) continue;
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
