using System.Text.RegularExpressions;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Resources;

namespace GodMode.Voice;

/// <summary>
/// What the bot says itself, not through the model: announcements, the greeting and a create's answer. In the session's
/// language, Danish, else English, until the user switches to the other one (<see cref="Heard"/>, #507), as the model
/// answers in theirs; then in theirs until they switch back.
/// </summary>
public sealed partial class VoicePhrases
{
    /// <summary>How long a prompt the read-back says as it is; a longer one is said cut, after its first words.</summary>
    public const int PromptReadBack = 100;

    private readonly bool _primaryDanish;
    private volatile bool _danish;

    public VoicePhrases(SessionLanguages languages) =>
        _danish = _primaryDanish = languages.Primary.StartsWith("da", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The user said <paramref name="text"/>, a final: the code's words are in its language from now on, when it tells one
    /// (<see cref="SpokenLanguage"/>): the other language's by a sentence of it (<see cref="SpokenLanguage.SwitchAway"/>),
    /// since a few words heard in it may be the session's misheard, and the session's own back by a word.
    /// </summary>
    public void Heard(string text)
    {
        var lean = SpokenLanguage.Lean(text);
        var towardsPrimary = _primaryDanish ? lean : -lean;
        if (towardsPrimary > 0)
            _danish = _primaryDanish;
        else if (towardsPrimary <= -SpokenLanguage.SwitchAway)
            _danish = !_primaryDanish;
    }

    /// <summary>Whether the code speaks Danish now.</summary>
    public bool Danish => _danish;

    /// <summary>
    /// What the session says as it starts listening: "Klar.", and, when there are servers and none answered in time,
    /// that it knows no project yet, so a project it does not know is not taken for one that is not there.
    /// </summary>
    public string Greeting(ServersHeard heard) => (heard.NoneAnswered, _danish) switch
    {
        (false, true) => "Klar.",
        (false, false) => "Ready.",
        (true, true) => "Klar. Ingen server svarer endnu.",
        (true, false) => "Ready. No server answers yet.",
    };

    /// <summary>Before several announcements said together: "3 venter på dig:".</summary>
    public string Several(int count) => _danish ? $"{count} venter på dig:" : $"{count} need you:";

    /// <summary>
    /// A project named alone (#450): its label, then its topic (#455), root and profile, those it is said with: "issue
    /// 376, mic-timeout i GodMode, profil Mega" / "issue 376, mic-timeout in GodMode, profile Mega".
    /// </summary>
    public string Named(SpokenName name) =>
        name.Label + (name.Topic is { } topic ? $", {topic}" : "") + (name.Root is { } root ? _danish ? $" i {root}" : $" in {root}" : "")
        + (name.Profile is { } profile ? _danish ? $", profil {profile}" : $", profile {profile}" : "");

    /// <summary>
    /// A project as a list names it (#507): its label, then its topic after "om" / "about", so that a list's commas part
    /// its projects, not a project from its topic, then its root and profile as <see cref="Named"/> says them: "issue 376
    /// om mic-timeout i GodMode".
    /// </summary>
    public string Listed(SpokenName name) =>
        Named(name with { Topic = null }) is var named && name.Topic is { } topic
            ? named.Insert(name.Label.Length, _danish ? $" om {topic}" : $" about {topic}")
            : Named(name);

    /// <summary>The project named as a sentence's subject: <see cref="Named"/>, with a comma after a topic or profile, before the verb.</summary>
    private string Subject(SpokenName name) => Named(name) + (name.Topic is null && name.Profile is null ? "" : ",");

    /// <summary>
    /// One project that needs the user, by its name (<see cref="Named"/>): short, since the model reads the rest when
    /// asked, or, when the session gave its own spoken reply, that reply word for word (<see cref="Spoken"/>). A finished
    /// turn is done only when the session said so, and idle otherwise (issue #467).
    /// </summary>
    /// <param name="choices">Whether a question's options are said after it (#529, <see cref="Choices(AttentionItem)"/>): when it is said alone.</param>
    public string Announce(SpokenName name, AttentionItem item, bool choices = true) =>
        Spoken(name, item, choices) ?? (choices && Choices(item) is { Length: > 0 } listed
            // Its options make sense only with the question: said as it is when it can be
            ? With(QuestionChoices.Single(item.Question)!.Question.Trim() is var question && VoiceTools.SaidAsIs(question)
                ? Reads(name, item, question) : AnnounceOnly(name, item), listed)
            : AnnounceOnly(name, item));

    private string AnnounceOnly(SpokenName name, AttentionItem item) => (Subject(name), item.Kind, _danish) switch
    {
        (var who, AttentionKind.Question, true) when item.Outcome == TurnOutcome.Blocked => $"{who} er blokeret",
        (var who, AttentionKind.Question, false) when item.Outcome == TurnOutcome.Blocked => $"{who} is blocked",
        (var who, AttentionKind.Question, true) => $"{who} har et spørgsmål",
        (var who, AttentionKind.Question, false) => $"{who} has a question",
        (var who, AttentionKind.Permission, true) => $"{who} skal have tilladelse: {PermissionSummary(item)}. Svar på skærmen",
        (var who, AttentionKind.Permission, false) => $"{who} needs permission: {PermissionSummary(item)}. Answer it on screen",
        (var who, AttentionKind.Error, true) => $"{who} fejlede",
        (var who, AttentionKind.Error, false) => $"{who} failed",
        (var who, AttentionKind.Escalation, true) => $"{who} har brug for din beslutning",
        (var who, AttentionKind.Escalation, false) => $"{who} needs your decision",
        (var who, AttentionKind.Review, true) => $"{who} har fået ændringsønsker",
        (var who, AttentionKind.Review, false) => $"{who} has changes requested",
        (var who, AttentionKind.Finished, true) when item.Outcome == TurnOutcome.Done => $"{who} er færdig",
        (var who, AttentionKind.Finished, false) when item.Outcome == TurnOutcome.Done => $"{who} is done",
        (var who, AttentionKind.Finished, true) => $"{who} er idle",
        (var who, AttentionKind.Finished, false) => $"{who} is idle",
    };

    /// <summary>
    /// The session's own spoken reply (<see cref="AttentionItem.Spoken"/>, issue #384), word for word, after a lead-in
    /// that names the project and what it needs: "issue 283 er færdig: …" (only when it said it is done, issue #467, else
    /// "issue 283 er idle: …"), "issue 283 spørger: …", "issue 283 er blokeret: …". The session wrote it,
    /// not the bot, so it never starts the line, where a "Sendt" in it would be the bot's own word (<see cref="SentNode"/>).
    /// Null when the session gave none.
    /// </summary>
    /// <param name="choices">Whether a question's options are said after it (#529, <see cref="Choices(AttentionItem)"/>).</param>
    public string? Spoken(SpokenName name, AttentionItem item, bool choices = true) =>
        item.Spoken is { Length: > 0 } spoken ? With(Reads(name, item, spoken), choices ? Choices(item) : "") : null;

    /// <summary>The line, then <paramref name="choices"/> as a sentence of their own (<see cref="Choices(AttentionItem)"/>), when there are any.</summary>
    public static string With(string line, string choices) =>
        choices.Length == 0 ? line : $"{GodModeAnnouncementFormatter.Sentence(line)} {choices}";

    /// <summary>
    /// A pending question's options (#529), their labels alone, said after the question: "Valg: Ja, opret dem; Ikke nu;
    /// eller Senere" / "Options: …; or …". Said for an AskUserQuestion of one question; nothing for any other item, or one of
    /// several questions, which the screen answers.
    /// </summary>
    public string Choices(AttentionItem item) => Choices(item.Kind == AttentionKind.Question ? item.Question : null);

    /// <inheritdoc cref="Choices(AttentionItem)"/>
    public string Choices(PendingQuestion? pending)
    {
        if (QuestionChoices.Single(pending) is not { } question)
            return "";
        var labels = question.Options.Select(o => o.Label.Trim().TrimEnd('.')).ToList();
        var or = _danish ? "eller" : "or";
        var listed = labels is [var one] ? one : $"{string.Join("; ", labels.Take(labels.Count - 1))}; {or} {labels[^1]}";
        return _danish ? $"Valg: {listed}" : $"Options: {listed}";
    }

    /// <summary>
    /// A project's own words read out, after a lead-in that names it and what it needs: "issue 283 spørger: …" for a
    /// question, "issue 283 er blokeret: …" for one it said it is blocked on, "issue 283 er færdig: …" for a finished
    /// turn it said is done, and "issue 283 er idle: …" for one that said nothing of the work (issue #467).
    /// </summary>
    public string Reads(SpokenName name, AttentionItem item, string text) => (item.Kind, item.Outcome, _danish) switch
    {
        (AttentionKind.Question, TurnOutcome.Blocked, true) => $"{Subject(name)} er blokeret: {text}",
        (AttentionKind.Question, TurnOutcome.Blocked, false) => $"{Subject(name)} is blocked: {text}",
        (AttentionKind.Question, _, true) => $"{Subject(name)} spørger: {text}",
        (AttentionKind.Question, _, false) => $"{Subject(name)} asks: {text}",
        (_, TurnOutcome.Done, true) => $"{Subject(name)} er færdig: {text}",
        (_, TurnOutcome.Done, false) => $"{Subject(name)} is done: {text}",
        (_, _, true) => $"{Subject(name)} er idle: {text}",
        (_, _, false) => $"{Subject(name)} is idle: {text}",
    };

    /// <summary>Where a project stands, in the recap the session keeps of it (#466), after its name: "issue 283: …".</summary>
    public string Stands(SpokenName name, string recap) => $"{Named(name)}: {recap}";

    /// <summary>
    /// That the project, which keeps no recap, has been asked for one (#513): "Jeg har bedt den om et resumé, som jeg læser
    /// næste gang du spørger.", or, asked before, that it has not come yet.
    /// </summary>
    public string RecapAsked(bool before) => (before, _danish) switch
    {
        (false, true) => "Jeg har bedt den om et resumé, som jeg læser næste gang du spørger.",
        (false, false) => "I've asked it for a recap, which I'll read the next time you ask.",
        (true, true) => "Den er bedt om et resumé, som ikke er kommet endnu.",
        (true, false) => "It has been asked for a recap, which hasn't come yet.",
    };

    /// <summary>A project's last spoken reply, when nothing of it needs the user (#466): "issue 283 sagde sidst: …".</summary>
    public string SaidLast(SpokenName name, string spoken) =>
        _danish ? $"{Subject(name)} sagde sidst: {spoken}" : $"{Subject(name)} said last: {spoken}";

    /// <summary>A project's last result, shortened, when nothing of it needs the user (#466): "Sidste resultat fra issue 283: …".</summary>
    public string LastResult(SpokenName name, string result) =>
        _danish ? $"Sidste resultat fra {Named(name)}: {result}" : $"Last result from {Named(name)}: {result}";

    /// <summary>
    /// What needs the user, as <see cref="VoiceTools.WhatNeedsMe"/> found it, in the code's words (#456): each project as
    /// its announcement says it (<see cref="Announce"/>), several after their count, "Intet venter." for none.
    /// </summary>
    /// <param name="overseers">The overseers whose workers need someone (#469), each said by its line (<see cref="Workers"/>) after the items.</param>
    public string Waiting(IReadOnlyList<(SpokenName Name, AttentionItem Item)> items, IReadOnlyList<(SpokenName Name, int Workers, int Waiting)>? overseers = null) =>
        items.Select(i => GodModeAnnouncementFormatter.Sentence(Announce(i.Name, i.Item, choices: items.Count == 1)))
            .Concat((overseers ?? []).Select(o => GodModeAnnouncementFormatter.Sentence(Workers(o.Name, o.Workers, o.Waiting)))).ToList() switch
        {
            [] => _danish ? "Intet venter." : "Nothing needs you.",
            [var one] => one,
            var several => $"{Several(several.Count)} {string.Join(" ", several)}",
        };

    /// <summary>
    /// A long what-needs-me (#507), summarised as a long list is (<see cref="ListSummary"/>): the count, then what they need,
    /// most urgent first, by name while they fit in a page, else counted; then the overseers' lines, those waiting from
    /// before a window left out (<see cref="WaitingFromBefore"/>), and "Mere?" when more follows. "8 venter på dig: issue
    /// 1 og issue 2 skal have tilladelse, 6 har et spørgsmål. Mere?"
    /// </summary>
    public string WaitingSummary(int count, IReadOnlyList<(WaitingKind Kind, int Count, IReadOnlyList<SpokenName> Named)> kinds,
        IReadOnlyList<(SpokenName Name, int Workers, int Waiting)> overseers, int fromBefore, bool more) =>
        $"{Several(count)} {string.Join(", ", kinds.Select(k => k.Named.Count > 0
            ? $"{And([.. k.Named.Select(Listed)])} {Needs(k.Kind, k.Named.Count)}"
            : $"{k.Count} {Needs(k.Kind, k.Count)}"))}."
        + string.Concat(overseers.Select(o => Then(GodModeAnnouncementFormatter.Sentence(Workers(o.Name, o.Workers, o.Waiting)))))
        + Then(WaitingFromBefore(fromBefore, saidAny: true))
        + (more ? $" {More}" : "");

    /// <summary>A page of a long what-needs-me (#507): each project as its announcement says it, "Mere?" when more follows.</summary>
    public string WaitingPage(IReadOnlyList<(SpokenName Name, AttentionItem Item)> page, bool more) =>
        string.Join(" ", page.Select(i => GodModeAnnouncementFormatter.Sentence(Announce(i.Name, i.Item, choices: false)))) + (more ? $" {More}" : "");

    /// <summary>What <paramref name="count"/> projects of the kind need, after their names or count: "har et spørgsmål", "need permission".</summary>
    private string Needs(WaitingKind kind, int count) => (kind, _danish, count == 1) switch
    {
        (WaitingKind.Permission, true, _) => "skal have tilladelse",
        (WaitingKind.Question, true, _) => "har et spørgsmål",
        (WaitingKind.Blocked, true, _) => "er blokeret",
        (WaitingKind.Escalation, true, _) => "har brug for din beslutning",
        (WaitingKind.Error, true, _) => "fejlede",
        (WaitingKind.Review, true, _) => "har fået ændringsønsker",
        (WaitingKind.Done, true, true) => "er færdig",
        (WaitingKind.Done, true, false) => "er færdige",
        (WaitingKind.Idle, true, _) => "er idle",
        (WaitingKind.Permission, false, true) => "needs permission",
        (WaitingKind.Permission, false, false) => "need permission",
        (WaitingKind.Question, false, true) => "has a question",
        (WaitingKind.Question, false, false) => "have a question",
        (WaitingKind.Blocked, false, true) => "is blocked",
        (WaitingKind.Blocked, false, false) => "are blocked",
        (WaitingKind.Escalation, false, true) => "needs your decision",
        (WaitingKind.Escalation, false, false) => "need your decision",
        (WaitingKind.Error, false, _) => "failed",
        (WaitingKind.Review, false, true) => "has changes requested",
        (WaitingKind.Review, false, false) => "have changes requested",
        (WaitingKind.Done, false, true) => "is done",
        (WaitingKind.Done, false, false) => "are done",
        (WaitingKind.Idle, false, true) => "is idle",
        (WaitingKind.Idle, false, false) => "are idle",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// An overseer's line for the workers it runs (#469), which voice leaves out: "voice-epics: 6 workers, 2 venter på den" /
    /// "voice-epics: 6 workers, 2 waiting on it". <paramref name="waiting"/> are those that need someone, which the overseer handles.
    /// </summary>
    public string Workers(SpokenName name, int workers, int waiting) => $"{Named(name)}: {RunsWorkers(workers, waiting)}";

    /// <summary>"6 workers, 2 venter på den" / "6 workers, 2 waiting on it"; "1 worker" when none waits.</summary>
    public string RunsWorkers(int workers, int waiting) =>
        (workers == 1 ? "1 worker" : $"{workers} workers") + (waiting, _danish) switch
        {
            (0, _) => "",
            (var n, true) => $", {n} venter på den",
            (var n, false) => $", {n} waiting on it",
        };

    /// <summary>
    /// The projects <see cref="VoiceTools.ListProjects"/> listed, in the code's words (#456): the count, then each group
    /// once, by its profile and root, with its projects by their labels. "3 projekter. Profil Mega, root GodMode: issue 376,
    /// issue 382. Profil Private, root voicebot: branch master."
    /// </summary>
    /// <param name="needs">
    /// What the listed projects need, each as its announcement says it (<see cref="Announce"/>), said after the groups:
    /// a list of an overseer's workers (#469) says their questions, which no announcement said.
    /// </param>
    public string Projects(IReadOnlyList<ListedGroup> groups, LeftOut? left = null,
        IReadOnlyList<(SpokenName Name, AttentionItem Item)>? needs = null)
    {
        var count = groups.Sum(g => g.Labels.Count);
        return count == 0
            ? left is { Count: > 0 } ? NothingNew(left) : _danish ? "Ingen projekter." : "No projects."
            : $"{Total(count)} {string.Join(" ", groups.Select(g => GroupLine(g.Profile, g.Root, g.Server, g.Labels)))}"
                + string.Concat((needs ?? []).Select(n => Then(GodModeAnnouncementFormatter.Sentence(Announce(n.Name, n.Item)))))
                + Then(left is null ? "" : LeftOut(left));
    }

    /// <summary>A sentence after another: a space before it; nothing for none.</summary>
    private static string Then(string sentence) => sentence.Length > 0 ? $" {sentence}" : "";

    /// <summary>
    /// What a list left out (#468), as its last sentence: "Og 9 gamle." for stale ones, "Og 2 uden nyt." for those with
    /// nothing new in the window the user asked for, and how many of those need the user from before (#507), as what
    /// needs me says "fra før": "Og 2 uden nyt. 1 af dem venter på dig." Empty when it left none out.
    /// </summary>
    public string LeftOut(LeftOut left) => (left.Count, left.Asked, _danish) switch
    {
        (> 0, true, true) when left.Waiting > 0 => $"Og {left.Count} uden nyt. {WaitingOfThem(left.Waiting, left.Count)}",
        (> 0, true, false) when left.Waiting > 0 => $"And {left.Count} with nothing new. {WaitingOfThem(left.Waiting, left.Count)}",
        (0, _, _) => "",
        (1, false, true) => "Og 1 gammel.",
        (var n, false, true) => $"Og {n} gamle.",
        (1, false, false) => "And 1 old one.",
        (var n, false, false) => $"And {n} old ones.",
        (var n, true, true) => $"Og {n} uden nyt.",
        (var n, true, false) => $"And {n} with nothing new.",
    };

    /// <summary>
    /// A list that left out every project it had (#468): "Intet nyt. 9 gamle." for stale ones, "Intet nyt." when the user
    /// asked for a window nothing happened in, with those that need the user from before (#507): "Intet nyt. 1 venter
    /// på dig fra før."
    /// </summary>
    public string NothingNew(LeftOut left) => (left.Count, left.Asked, _danish) switch
    {
        (_, true, true) when left.Waiting > 0 => $"Intet nyt. {left.Waiting} venter på dig fra før.",
        (_, true, false) when left.Waiting == 1 => "Nothing new. 1 needs you from before.",
        (_, true, false) when left.Waiting > 0 => $"Nothing new. {left.Waiting} need you from before.",
        (_, true, true) => "Intet nyt.",
        (_, true, false) => "Nothing new.",
        (1, false, true) => "Intet nyt. 1 gammel.",
        (var n, false, true) => $"Intet nyt. {n} gamle.",
        (1, false, false) => "Nothing new. 1 old one.",
        (var n, false, false) => $"Nothing new. {n} old ones.",
    };

    /// <summary>"1 af dem venter på dig." / "1 of them needs you.", of <paramref name="of"/> left out; "Den venter på dig." for the only one.</summary>
    private string WaitingOfThem(int waiting, int of) => (waiting, of, _danish) switch
    {
        (1, 1, true) => "Den venter på dig.",
        (1, 1, false) => "It needs you.",
        (var n, _, true) => $"{n} af dem venter på dig.",
        (1, _, false) => "1 of them needs you.",
        (var n, _, false) => $"{n} of them need you.",
    };

    /// <summary>
    /// What needs the user that the window asked for left out (#468): "Og 2 fra før." after what it said, "Intet nyt
    /// venter. 2 venter fra før." when it said none. Empty when it left none out.
    /// </summary>
    public string WaitingFromBefore(int count, bool saidAny) => (count, saidAny, _danish) switch
    {
        (0, _, _) => "",
        (var n, true, true) => $"Og {n} fra før.",
        (var n, true, false) => $"And {n} from before.",
        (var n, false, true) => $"Intet nyt venter. {n} venter fra før.",
        (1, false, false) => "Nothing new needs you. 1 needs you from before.",
        (var n, false, false) => $"Nothing new needs you. {n} need you from before.",
    };

    /// <summary>"3 projekter." / "3 projects.".</summary>
    private string Total(int count) => (count, _danish) switch
    {
        (1, true) => "1 projekt.",
        (_, true) => $"{count} projekter.",
        (1, false) => "1 project.",
        (_, false) => $"{count} projects.",
    };

    /// <summary>
    /// A group of a list, by its profile and root, and its server when another server's is said alike (#507): "Profil
    /// Mega, root GodMode: issue 376, issue 382.", "Profil Mega, root GodMode, server work-pc: issue 1.".
    /// </summary>
    private string GroupLine(string profile, string? root, string? server, IEnumerable<string> labels) =>
        $"{(_danish ? "Profil" : "Profile")} {profile}{(root is { } r ? $", root {r}" : "")}{(server is { } s ? $", server {s}" : "")}: {string.Join(", ", labels)}.";

    /// <summary>The question that ends a part when more follows, which "mere" answers.</summary>
    public string More => _danish ? "Mere?" : "More?";

    /// <summary>
    /// A long project list, summarised by state (#457, <see cref="ProjectListing"/>): the count, then each state, its
    /// projects by name when it names them, else their count, "the rest" for the one state counted after all named, when
    /// it is said last (#507): before a state named after it, "resten" would not be the rest. "16 projekter. branch
    /// master er idle, resten er stoppet. Mere?"
    /// </summary>
    public string ListSummary(int count, IReadOnlyList<(ListedState State, int Count, IReadOnlyList<SpokenName> Named)> states, bool more, LeftOut? left = null)
    {
        var counted = states.Select((s, i) => (s, i)).Where(x => x.s.Named.Count == 0).ToList();
        var rest = counted is [var only] && only.i > 0 && only.i == states.Count - 1 ? only.s.State : (ListedState?)null;
        var parts = states.Select(s =>
            s.Named.Count > 0 ? $"{And([.. s.Named.Select(Listed)])} {Doing(s.State, s.Named.Count)}"
            : s.State == rest ? $"{(_danish ? "resten" : "the rest")} {Doing(s.State, 2)}"
            : $"{s.Count} {Doing(s.State, s.Count)}");
        return $"{Total(count)} {string.Join(", ", parts)}.{Then(left is null ? "" : LeftOut(left))}{(more ? $" {More}" : "")}";
    }

    /// <summary>
    /// A page of a long project list (#457): its projects by profile and root, each state said before its first, "Mere?"
    /// when more follows. "Stoppet. Profil Mega, root GodMode: issue 1, issue 2. Mere?"
    /// </summary>
    public string ListPage(IReadOnlyList<ListedProject> page, bool more)
    {
        List<string> sentences = [];
        ListedState? state = null;
        for (var i = 0; i < page.Count;)
        {
            var first = page[i];
            var group = page.Skip(i).TakeWhile(p => p.State == first.State && p.Profile == first.Profile && p.Root == first.Root && p.Server == first.Server).ToList();
            if (first.State != state)
                sentences.Add($"{Heading(first.State)}.");
            state = first.State;
            sentences.Add(GroupLine(first.Profile, first.Root, first.Server, group.Select(p => p.Label)));
            i += group.Count;
        }
        return string.Join(" ", sentences) + (more ? $" {More}" : "");
    }

    /// <summary>What <paramref name="count"/> projects in the state are doing, after their names or count: "er stoppet", "are stopped".</summary>
    private string Doing(ListedState state, int count) => (state, _danish, count == 1) switch
    {
        (ListedState.NeedsYou, true, _) => "venter på dig",
        (ListedState.Running, true, _) => "kører",
        (ListedState.Idle, true, _) => "er idle",
        (ListedState.Stopped, true, _) => "er stoppet",
        (ListedState.NeedsYou, false, true) => "needs you",
        (ListedState.Running, false, true) => "is running",
        (ListedState.Idle, false, true) => "is idle",
        (ListedState.Stopped, false, true) => "is stopped",
        (ListedState.NeedsYou, false, false) => "need you",
        (ListedState.Running, false, false) => "are running",
        (ListedState.Idle, false, false) => "are idle",
        (ListedState.Stopped, false, false) => "are stopped",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    /// <summary>A state as a page's heading: "Venter på dig", "Kører", "Idle", "Stoppet".</summary>
    private string Heading(ListedState state) => (state, _danish) switch
    {
        (ListedState.NeedsYou, true) => "Venter på dig",
        (ListedState.Running, true) => "Kører",
        (ListedState.Idle, _) => "Idle",
        (ListedState.Stopped, true) => "Stoppet",
        (ListedState.NeedsYou, false) => "Needs you",
        (ListedState.Running, false) => "Running",
        (ListedState.Stopped, false) => "Stopped",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    /// <summary>"a", "a og b", "a, b og c" / "a, b and c".</summary>
    private string And(IReadOnlyList<string> items) =>
        items.Count == 1 ? items[0] : $"{string.Join(", ", items.Take(items.Count - 1))} {(_danish ? "og" : "and")} {items[^1]}";

    /// <summary>
    /// A create read back, as the question its yes answers: the root, its profile (and server, when there are several),
    /// the action, and what will be made, its prompt said as it is when short, and cut when long (#449). It holds no
    /// yes-word (<see cref="ConfirmCreateNode.HoldsYes"/>), so its echo can never answer it: a prompt that holds one is
    /// only said to be there. An action taken from the issue's label, in place of the one asked for, is said first (#473):
    /// "Issue 471 er mærket epic. Skal jeg oprette issue 471 i GodMode, profil Godmode, som epic?".
    /// </summary>
    public string ReadBack(CreateRequest request) =>
        (request is { Label: { } label, Issue: { } issue } ? _danish ? $"Issue {issue} er mærket {label}. " : $"Issue {issue} is labelled {label}. " : "")
        + Question(request);

    private string Question(CreateRequest request)
    {
        var root = request.Root;
        var where = _danish
            ? $"i {root.Shown}, profil {root.Profile}{(request.SeveralServers ? $", server {root.ServerName}" : "")}, som {request.Action.Name}"
            : $"in {root.Shown}, profile {root.Profile}{(request.SeveralServers ? $", server {root.ServerName}" : "")}, as {request.Action.Name}";
        if (request is { Issue: null, Name: { } named, Prompt: { } prompt } && Said(prompt) is { } quoted)
            return _danish
                ? $"Skal jeg oprette {named} {where}, med beskrivelsen \"{quoted}\"?"
                : $"Shall I create {named} {where}, with the prompt \"{quoted}\"?";

        var what = (request.Issue, request.Name, request.WithPrompt, _danish) switch
        {
            ({ } issue, _, _, _) => $"issue {issue}",
            (null, { } name, true, true) => $"{name} med beskrivelse",
            (null, { } name, false, true) => $"{name} uden beskrivelse",
            (null, { } name, true, false) => $"{name} with a description",
            (null, { } name, false, false) => $"{name} with no description",
            (null, { } name, null, _) => name,
            (null, null, _, true) => "en session",
            (null, null, _, false) => "a session",
        };
        return _danish ? $"Skal jeg oprette {what} {where}?" : $"Shall I create {what} {where}?";
    }

    /// <summary>
    /// A prompt as the read-back says it: as it is, up to <see cref="PromptReadBack"/>, else its first words and "…";
    /// its last full stop left to the question. Null when it holds a yes-word, which the read-back must not.
    /// </summary>
    internal static string? Said(string prompt)
    {
        var text = Whitespace().Replace(prompt.Trim(), " ").TrimEnd('.', ' ');
        if (text.Length > PromptReadBack)
            text = (text.LastIndexOf(' ', PromptReadBack - 1) is var space and > 0 ? text[..space] : text[..PromptReadBack]).TrimEnd(',', ';', ':', '.', ' ') + " …";
        return text.Length == 0 || ConfirmCreateNode.HoldsYes(text) ? null : text;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// Which of two projects an answer naming none is for, as a closed question naming both (#461): the one talked
    /// about, then the one an announcement just named. "Til issue 283 eller issue 101?".
    /// </summary>
    public string Which(SpokenName before, SpokenName announced) =>
        _danish ? $"Til {Subject(before)} eller {Named(announced)}?" : $"To {Subject(before)} or {Named(announced)}?";

    /// <summary>Answers went out this turn (<see cref="SentNode"/>): "Sendt til issue 283.", "Sendt til issue 283 og issue 101.".</summary>
    public string Sent(IReadOnlyList<SpokenName> names)
    {
        var to = And([.. names.Select(Named).Distinct()]);
        return _danish ? $"Sendt til {to}." : $"Sent to {to}.";
    }

    /// <summary>A dictation started (#459): the project it goes to, and how it ends.</summary>
    public string DictationStarted(SpokenName name) =>
        _danish ? $"Diktat til {Named(name)}. Sig diktat slut, eller annullér diktat." : $"Dictating to {Named(name)}. Say end dictation, or cancel dictation.";

    /// <summary>
    /// A dictation sent (#459), read back: its length in sentences, and its first words, as it starts going out:
    /// "Sender 4 sætninger til issue 283, der starter: Brug den eksisterende migration …". To a project that is
    /// <paramref name="running"/> (#530), it says so, as it answers no question: the turn takes it in as it goes. A
    /// dictated sentence that sounds like a <paramref name="command"/> (<see cref="Dictation.LooksLikeCommand"/>) is named.
    /// </summary>
    public string DictationSending(SpokenName name, int sentences, string start, bool running, string? command)
    {
        List<string> said =
        [
            _danish
                ? $"Sender {sentences} {(sentences == 1 ? "sætning" : "sætninger")} til {Named(name)}, der starter: {GodModeAnnouncementFormatter.Sentence(start)}"
                : $"Sending {sentences} {(sentences == 1 ? "sentence" : "sentences")} to {Named(name)}, starting: {GodModeAnnouncementFormatter.Sentence(start)}",
        ];
        if (command is not null)
            said.Add(_danish ? $"En sætning lyder som en kommando: {GodModeAnnouncementFormatter.Sentence(command)}"
                : $"One sentence sounds like a command: {GodModeAnnouncementFormatter.Sentence(command)}");
        if (running)
            said.Add(_danish ? "Den arbejder, og tager det med undervejs." : "It is working, and takes it in as it goes.");
        return string.Join(" ", said);
    }

    /// <summary>"Diktér til" the project dictated to, said while dictating (#530): the dictation goes on.</summary>
    public string DictationGoesOn(SpokenName name) =>
        _danish ? $"Diktatet til {Named(name)} fortsætter." : $"Still dictating to {Named(name)}.";

    /// <summary>"Diktér til" another project, said while dictating (#530): nothing of it is taken, and this one ends first.</summary>
    public string DictationElsewhere(SpokenName name) => _danish
        ? $"Du dikterer stadig til {Named(name)}, intet tilføjet. Sig diktat slut eller annullér diktat først."
        : $"Still dictating to {Named(name)}, nothing added. Say end dictation or cancel dictation first.";

    /// <summary>A dictation dropped by the user ("annullér diktat"): nothing went out.</summary>
    public string DictationCancelled(SpokenName name) =>
        _danish ? $"Annulleret. Intet sendt til {Named(name)}." : $"Cancelled. Nothing sent to {Named(name)}.";

    /// <summary>A dictation that waited too long for more words, dropped: nothing went out.</summary>
    public string DictationDropped(SpokenName name) =>
        _danish ? $"Diktatet til {Named(name)} er droppet. Intet sendt." : $"The dictation to {Named(name)} was dropped. Nothing sent.";

    /// <summary>"Diktat slut" with nothing dictated: the dictation goes on.</summary>
    public string DictationEmpty(SpokenName? name) => (name, _danish) switch
    {
        (null, true) => "Intet at sende.",
        (null, false) => "Nothing to send.",
        ({ } n, true) => $"Intet dikteret til {Named(n)} endnu. Diktér, eller annullér diktat.",
        ({ } n, false) => $"Nothing dictated to {Named(n)} yet. Dictate, or cancel dictation.",
    };

    /// <summary>A dictation to a project no handle names: none is started.</summary>
    public string DictationUnknown(string said) =>
        _danish ? $"Ukendt projekt: {said}. Intet diktat." : $"Unknown project: {said}. No dictation.";

    /// <summary>A dictation to a project that waits on a permission, answered on screen only.</summary>
    public string DictationPermission(SpokenName name, string? summary) => (summary, _danish) switch
    {
        ({ } what, true) => $"{Subject(name)} skal have tilladelse: {what}. Svar på skærmen.",
        ({ } what, false) => $"{Subject(name)} needs permission: {what}. Answer it on screen.",
        (null, true) => $"{Subject(name)} venter på en tilladelse. Svar på skærmen.",
        (null, false) => $"{Subject(name)} waits on a permission. Answer it on screen.",
    };

    /// <summary>A dictation to a project that failed to create: it has no session to send to.</summary>
    public string DictationCreateFailed(SpokenName name) =>
        _danish ? $"{Subject(name)} blev ikke oprettet, og kan ikke få svar." : $"{Subject(name)} failed to create, and takes no answer.";

    /// <summary>A dictation that cannot go now, and is kept: why, and how to go on.</summary>
    public string DictationHeld(SpokenName name, string why) =>
        _danish ? $"Intet sendt. {why} Sig diktat slut igen, eller annullér diktat." : $"Nothing sent. {why} Say end dictation again, or cancel dictation.";

    /// <summary>The project's server did not answer: a dictation <paramref name="started"/> is kept, and none is started otherwise.</summary>
    public string DictationNotReached(SpokenName name, bool started) => (started, _danish) switch
    {
        (true, true) => $"Intet sendt. {Subject(name)} svarer ikke. Sig diktat slut igen, eller annullér diktat.",
        (true, false) => $"Nothing sent. {Subject(name)} does not answer. Say end dictation again, or cancel dictation.",
        (false, true) => $"{Subject(name)} svarer ikke. Intet diktat.",
        (false, false) => $"{Subject(name)} does not answer. No dictation.",
    };

    /// <summary>The model claimed a send, and none went out this turn (<see cref="SentNode"/>).</summary>
    public string NothingSent => _danish ? "Intet sendt. Sig svaret igen." : "Nothing was sent. Say the answer again.";

    /// <summary>A yes after the read-back it would have answered was dropped (it timed out, or the bot said something else).</summary>
    public string NothingToConfirm => _danish ? "Der venter ingen oprettelse. Sig start igen." : "Nothing waits to be created. Say start again.";

    /// <summary>The user said yes to a create read back: it runs, and <see cref="Created"/> says when it is done.</summary>
    public string Creating => _danish ? "Opretter." : "Creating.";

    /// <summary>The user said anything but yes to a create read back.</summary>
    public string CreateCancelled => _danish ? "Annulleret. Intet oprettet." : "Cancelled. Nothing created.";

    /// <summary>A create is done: the new session by its handle, or that it failed (<see cref="Failed"/>).</summary>
    public string Created(CreateOutcome outcome) => (outcome, _danish) switch
    {
        ({ Handle: { } handle }, true) => $"{handle} er oprettet",
        ({ Handle: { } handle }, false) => $"{handle} is created",
        ({ Error: { } error }, _) => Failed(outcome.Request, error),
        (_, true) => $"Færdig i {outcome.Request.Root.Shown}",
        (_, false) => $"Done in {outcome.Request.Root.Shown}",
    };

    /// <summary>How long a server's error may be to be said as it is: one short sentence.</summary>
    public const int ErrorSaid = 80;

    /// <summary>
    /// A create that failed, said short (#449): what (the issue, else the action), where (profile / root, and the server
    /// when there are several) and why, in a few words. A short error is said as it is; a script's failure as that
    /// script failing, and anything longer not at all: the log has it, and the app shows it whole.
    /// </summary>
    public string Failed(CreateRequest request, string error)
    {
        var what = request.Issue is { } issue ? $"issue {issue}" : _danish ? request.Action.Name : $"the {request.Action.Name}";
        var where = $"{request.Root.Profile} / {request.Root.Shown}{(request.SeveralServers ? $"{(_danish ? " på" : " on")} {request.Root.ServerName}" : "")}";
        var lead = _danish ? $"Kunne ikke oprette {what} i {where}" : $"Could not create {what} in {where}";
        error = error.Trim();
        if (ScriptFailure().Match(error) is { Success: true } script)
        {
            var name = Path.GetFileNameWithoutExtension(script.Groups["script"].Value.Replace('\\', '/').Split('/')[^1]);
            return _danish ? $"{lead}: {name}-scriptet fejlede. Loggen har resten" : $"{lead}: its {name} script failed. The log has the details";
        }
        return error.Length is > 0 and <= ErrorSaid && !error.Contains('\n') && !error.Contains('\\') && !error.Contains('/')
            ? $"{lead}: {error.TrimEnd('.')}"
            : _danish ? $"{lead}. Loggen har resten" : $"{lead}. The log has the details";
    }

    /// <summary>A root script's failure, as the server's ScriptRunner words it.</summary>
    [GeneratedRegex(@"^Script '(?<script>[^']+)' exited with code")]
    private static partial Regex ScriptFailure();

    private static string PermissionSummary(AttentionItem item) => item.Permission?.Summary ?? item.Text;
}
