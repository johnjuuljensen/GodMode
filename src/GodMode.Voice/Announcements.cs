using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using Microsoft.Extensions.Logging;
using VoiceBot.Core.Pipeline;
using VoiceBot.Core.Resources;

namespace GodMode.Voice;

/// <summary>
/// What the conversation is about: the project an answer goes to when the user names none. It is the project last
/// announced alone, read out, or answered. An announcement of several projects at once leaves it unset, so a reply
/// then must name one. An announcement that changed it is kept for <see cref="AnnouncedSwitchWindow"/> after it was
/// said (#461): an answer with no name then asks which project first (<see cref="TakeAnnouncedSwitch"/>).
/// </summary>
public sealed class VoiceConversation(TimeProvider? time = null)
{
    /// <summary>
    /// How long after an announcement that changed the current project was said an answer that names none asks which
    /// project it is for (#461). Counted from the end of its speech: an answer the user had begun before it, or began
    /// over it, reaches the tool after their words end, the transcript is final, and the model has called it, which is
    /// two to four seconds; five leaves a margin and still lets a "ja" said deliberately a little later go through.
    /// </summary>
    public static readonly TimeSpan AnnouncedSwitchWindow = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private Topic _topic = new(null, null);

    /// <summary>The project talked about. Setting it (a tool read, answered or named one) drops an announced switch.</summary>
    public ProjectRef? Current
    {
        get => Volatile.Read(ref _topic).Project;
        set => Volatile.Write(ref _topic, new Topic(value, null));
    }

    /// <summary>
    /// An announcement of <paramref name="project"/> alone (null for several) is being said: it is what the
    /// conversation is about now. When it changes the project from another one, the switch is kept: its window runs
    /// from now until the speech ends (<see cref="SpeechEnded"/>), then for <see cref="AnnouncedSwitchWindow"/>. An
    /// announcement of the project already current keeps whatever switch there was.
    /// </summary>
    public void Announced(ProjectRef? project)
    {
        var before = Volatile.Read(ref _topic);
        Volatile.Write(ref _topic, (before.Project, project) switch
        {
            ({ } from, { } to) when from == to => before,
            ({ } from, { } to) => new Topic(to, new AnnouncedSwitch(from, to, null)),
            _ => new Topic(project, null),
        });
    }

    /// <summary>The session stopped speaking: an announced switch's window starts now, unless it started already.</summary>
    public void SpeechEnded()
    {
        var topic = Volatile.Read(ref _topic);
        if (topic.Switched is { Said: null } switched)
            Interlocked.CompareExchange(ref _topic, topic with { Switched = switched with { Said = _time.GetUtcNow() } }, topic);
    }

    /// <summary>
    /// The announced switch an answer naming no project must not go through (#461): one being said, or said less than
    /// <see cref="AnnouncedSwitchWindow"/> ago. It is taken: asked about once, the next unnamed answer goes to the
    /// current project. Null when there is none.
    /// </summary>
    public AnnouncedSwitch? TakeAnnouncedSwitch()
    {
        var topic = Volatile.Read(ref _topic);
        if (topic.Switched is not { } switched) return null;
        Interlocked.CompareExchange(ref _topic, topic with { Switched = null }, topic);
        return switched.Said is not { } said || _time.GetUtcNow() - said < AnnouncedSwitchWindow ? switched : null;
    }

    private sealed record Topic(ProjectRef? Project, AnnouncedSwitch? Switched);

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

    private long _lastListed;

    /// <summary>
    /// When this voice session last listed projects (<see cref="VoiceTools.ListProjects"/>, <see cref="VoiceTools.WhatNeedsMe"/>):
    /// what "siden jeg sidst spurgte" lists from (#468). Null before any list. The session's own: another session, on
    /// this machine or another, keeps its own.
    /// </summary>
    public DateTime? LastListed
    {
        get => Interlocked.Read(ref _lastListed) is var ticks and > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;
        set => Interlocked.Exchange(ref _lastListed, value?.ToUniversalTime().Ticks ?? 0);
    }

    private PagedReading? _reading;

    /// <summary>
    /// What is being read in parts, and the part to read next: what "mere" and "læs videre" read on from
    /// (<see cref="VoiceTools.ReadMoreAsync"/>). One for both: a reply (<see cref="VoiceTools.ReadReplyAsync"/>) or a long
    /// project list (<see cref="VoiceTools.ListProjectsText"/>, #457), whichever was read last. Null before any was read; a
    /// new reply read or list said replaces it (a list said whole leaves none), and a new reply of its project drops a reply.
    /// </summary>
    public PagedReading? Reading
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

    private SaidByCode? _said;

    /// <summary>
    /// A tool's <paramref name="result"/> that the code says itself as <paramref name="said"/> (#456): the model's round
    /// after it is not run (<see cref="CodeSaysInference"/>). The last one a tool gave replaces any before it.
    /// </summary>
    public void SaysItself(string result, string said) => Volatile.Write(ref _said, new SaidByCode(result, said));

    /// <summary>What the code says for <paramref name="result"/>, the tool result the model would read next, and nothing from now on; null when it says nothing for it.</summary>
    public string? TakeSaid(string result) =>
        Volatile.Read(ref _said) is { } said && said.Result == result && Interlocked.CompareExchange(ref _said, null, said) == said ? said.Said : null;

    private sealed record SaidByCode(string Result, string Said);
}

/// <summary>
/// An announcement changed the project talked about from <paramref name="From"/> to <paramref name="To"/>; its speech
/// ended at <paramref name="Said"/>, null while it is still being said.
/// </summary>
public sealed record AnnouncedSwitch(ProjectRef From, ProjectRef To, DateTimeOffset? Said);

/// <summary>
/// A project's reply in the parts voice reads it in, and the index of the part to read next (its count once all were
/// read). <paramref name="Replies"/> are the replies it was read from, the last <paramref name="Turns"/>: while the
/// project's are still these, the parts are what it said last.
/// </summary>
public sealed record ReplyReading(ProjectRef Project, string Handle, IReadOnlyList<string> Parts, int Next, int Turns, IReadOnlyList<AssistantReply> Replies)
    : PagedReading;

/// <summary>What "mere" reads on in (<see cref="VoiceConversation.Reading"/>): a reply, or a long project list.</summary>
public abstract record PagedReading;

/// <summary>
/// A long project list's pages (#457), as they were when it was said, and the index of the page to read next (their
/// count once all were read). Each page is its text for the model and what the code says of it.
/// </summary>
public sealed record ListReading(IReadOnlyList<(string Result, string Said)> Pages, int Next) : PagedReading;

/// <summary>
/// GodMode's wording of the announcements queued up to a pause: one as it is, several after "3 venter på dig:". The
/// project it names is what the conversation is about next (<see cref="VoiceConversation"/>), none when it names several.
/// An announcement of no project (a create's outcome) leaves the conversation as it is, said alone.
/// Each is checked again here, when it is about to be said, not only when it was queued (#462): an item that no longer
/// needs the user (<see cref="AttentionBoard.Waits"/>) is dropped, and leaves the conversation as it is. Those still
/// waiting are said most urgent first: an important project's (#438), then by what they need
/// (<see cref="Urgency"/>), else in the order they came. What is being said already is never cut off for them: only
/// what waits for the pause is ordered.
/// </summary>
public sealed class GodModeAnnouncementFormatter(VoicePhrases phrases, VoiceConversation conversation, AttentionBoard? board = null)
    : IAnnouncementFormatter
{
    public string Format(IReadOnlyList<Announcement> announcements, SessionLanguages languages)
    {
        var waiting = announcements.Select(a => (Announcement: a, Item: ItemOf(a)))
            .Where(a => board?.Waits(a.Announcement) != false)
            .OrderByDescending(a => a.Item?.Item.Alert == AttentionAlert.Interrupt)
            .ThenBy(a => Urgency(a.Item?.Item.Kind))
            .ToList();
        string[] texts = [.. waiting.Select(a => Sentence(a.Announcement.Text)).Where(t => t.Length > 0)];
        // A dropped announcement was never said: the conversation stays where it was (#461's switch included)
        var projects = waiting.Select(a => a.Announcement.Source).Distinct().ToList();
        if (projects is not ([] or [null]))
            conversation.Announced(projects is [var only] ? ProjectRef.FromKey(only) : null);

        return texts switch
        {
            [] => "",
            [var one] => one,
            _ => $"{phrases.Several(texts.Length)} {string.Join(" ", texts)}",
        };
    }

    /// <summary>
    /// How soon an item of <paramref name="kind"/> is said among others, lowest first (#462): a permission, a question
    /// or an escalation holds its session until the user answers, an error stopped it, a review and a result wait. An
    /// announcement of no item (a create's outcome) comes last.
    /// </summary>
    internal static int Urgency(AttentionKind? kind) => kind switch
    {
        AttentionKind.Permission or AttentionKind.Question or AttentionKind.Escalation => 0,
        AttentionKind.Error => 1,
        AttentionKind.Review => 2,
        AttentionKind.Finished => 3,
        _ => 4,
    };

    /// <summary>The item the announcement is of, as the board has it now; null for one of no project.</summary>
    private ServerAttentionItem? ItemOf(Announcement announcement) =>
        board != null && ProjectRef.FromKey(announcement.Source) is { } project ? board.ItemOf(project) : null;

    /// <summary>The text as a sentence: ended with its own '?' or '!' (a session's spoken reply has them), else a '.'.</summary>
    internal static string Sentence(string text) =>
        text.Trim().TrimEnd('.') is { Length: > 0 } t ? t[^1] is '?' or '!' or '…' ? t : t + "." : "";
}

/// <summary>
/// The session's announcements, held while a create or a question waits on the user (<see cref="SessionCreates.Waiting"/>,
/// #473): an announcement between a read-back and its yes would take the yes's place. They are said in order once
/// nothing waits (the user answered, or the wait expired). A read-back to be said again (<see cref="SessionCreates.Repeat"/>)
/// is never held: it is what was waited on.
/// </summary>
public sealed class HeldAnnouncements
{
    private readonly ChannelWriter<Announcement> _session;
    private readonly SessionCreates _creates;
    private readonly Lock _lock = new();
    private readonly List<Announcement> _held = [];

    public HeldAnnouncements(ChannelWriter<Announcement> session, SessionCreates creates)
    {
        _session = session;
        _creates = creates;
        creates.Released += Release;
        creates.Repeat += readBack => session.TryWrite(new Announcement(readBack));
    }

    /// <summary>What is held now, oldest first.</summary>
    public IReadOnlyList<Announcement> Held
    {
        get
        {
            lock (_lock) return [.. _held];
        }
    }

    /// <summary>Says <paramref name="announcement"/> at the next pause, or holds it while a create or question waits.</summary>
    public void Write(Announcement announcement)
    {
        lock (_lock)
        {
            if (_held.Count > 0 || _creates.Waiting)
            {
                _held.Add(announcement);
                return;
            }
        }
        _session.TryWrite(announcement);
    }

    private void Release()
    {
        List<Announcement> held;
        lock (_lock)
        {
            if (_creates.Waiting) return;
            held = [.. _held];
            _held.Clear();
        }
        foreach (var announcement in held)
            _session.TryWrite(announcement);
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
    // By the announcement itself, not by its value: two of the same words are two announcements
    private readonly ConditionalWeakTable<Announcement, ServerAttentionItem> _announcements = new();

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
    /// The announcement of <paramref name="item"/>, saying <paramref name="text"/>: the board remembers which item it is
    /// of, so that it is checked again when it is about to be said (<see cref="Waits"/>).
    /// </summary>
    public Announcement AnnouncementOf(ServerAttentionItem item, string text)
    {
        var announcement = new Announcement(text, item.Project.Key);
        _announcements.AddOrUpdate(announcement, item);
        return announcement;
    }

    /// <summary>
    /// Whether the item <paramref name="announcement"/> is of still needs the user, as the board has it now (#462): it
    /// is still its project's item, and still announced. False once it was answered, on screen or by voice, marked
    /// seen, or the session moved on (its project needs something else now, or nothing), or it went to the inbox alone.
    /// An announcement the board did not make (<see cref="AnnouncementOf"/>), of no item, always waits.
    /// </summary>
    public bool Waits(Announcement announcement) =>
        !_announcements.TryGetValue(announcement, out var item)
        || ItemOf(item.Project) is { } now && Key(now) == Key(item) && now.Item.Alert != AttentionAlert.Inbox;

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
