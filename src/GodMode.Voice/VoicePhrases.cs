using System.Text.RegularExpressions;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Resources;

namespace GodMode.Voice;

/// <summary>What the bot says itself, not through the model: announcements, the greeting and a create's answer. Danish, else English.</summary>
public sealed partial class VoicePhrases
{
    /// <summary>How long a prompt the read-back says as it is; a longer one is said cut, after its first words.</summary>
    public const int PromptReadBack = 100;

    private readonly bool _danish;

    public VoicePhrases(SessionLanguages languages) =>
        _danish = languages.Primary.StartsWith("da", StringComparison.OrdinalIgnoreCase);

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
    /// A project named alone (#450): its label, then its root and profile, those it is said with: "issue 376 i GodMode,
    /// profil Mega" / "issue 376 in GodMode, profile Mega".
    /// </summary>
    public string Named(SpokenName name) =>
        name.Label + (name.Root is { } root ? _danish ? $" i {root}" : $" in {root}" : "")
        + (name.Profile is { } profile ? _danish ? $", profil {profile}" : $", profile {profile}" : "");

    /// <summary>The project named as a sentence's subject: <see cref="Named"/>, with a comma after a profile, before the verb.</summary>
    private string Subject(SpokenName name) => Named(name) + (name.Profile is null ? "" : ",");

    /// <summary>
    /// One project that needs the user, by its name (<see cref="Named"/>): short, since the model reads the rest when
    /// asked, or, when the session gave its own spoken reply, that reply word for word (<see cref="Spoken"/>). A finished
    /// turn is done only when the session said so, and idle otherwise (issue #467).
    /// </summary>
    public string Announce(SpokenName name, AttentionItem item) => Spoken(name, item) ?? (Subject(name), item.Kind, _danish) switch
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
    public string? Spoken(SpokenName name, AttentionItem item) => item.Spoken is { Length: > 0 } spoken ? Reads(name, item, spoken) : null;

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
    public string Waiting(IReadOnlyList<(SpokenName Name, AttentionItem Item)> items) =>
        items.Select(i => GodModeAnnouncementFormatter.Sentence(Announce(i.Name, i.Item))).ToList() switch
        {
            [] => _danish ? "Intet venter." : "Nothing needs you.",
            [var one] => one,
            var several => $"{Several(several.Count)} {string.Join(" ", several)}",
        };

    /// <summary>
    /// The projects <see cref="VoiceTools.ListProjects"/> listed, in the code's words (#456): the count, then each group
    /// once, by its profile and root, with its projects by their labels. "3 projekter. Profil Mega, root GodMode: issue 376,
    /// issue 382. Profil Private, root voicebot: branch master."
    /// </summary>
    public string Projects(IReadOnlyList<(string Profile, string? Root, IReadOnlyList<string> Labels)> groups)
    {
        var count = groups.Sum(g => g.Labels.Count);
        return count == 0
            ? _danish ? "Ingen projekter." : "No projects."
            : $"{Total(count)} {string.Join(" ", groups.Select(g => GroupLine(g.Profile, g.Root, g.Labels)))}";
    }

    /// <summary>"3 projekter." / "3 projects.".</summary>
    private string Total(int count) => (count, _danish) switch
    {
        (1, true) => "1 projekt.",
        (_, true) => $"{count} projekter.",
        (1, false) => "1 project.",
        (_, false) => $"{count} projects.",
    };

    /// <summary>A group of a list, by its profile and root: "Profil Mega, root GodMode: issue 376, issue 382.".</summary>
    private string GroupLine(string profile, string? root, IEnumerable<string> labels) =>
        $"{(_danish ? "Profil" : "Profile")} {profile}{(root is { } r ? $", root {r}" : "")}: {string.Join(", ", labels)}.";

    /// <summary>The question that ends a part when more follows, which "mere" answers.</summary>
    public string More => _danish ? "Mere?" : "More?";

    /// <summary>
    /// A long project list, summarised by state (#457, <see cref="ProjectListing"/>): the count, then each state, its
    /// projects by name when it names them, else their count, "the rest" for the one state counted after all named. "16
    /// projekter. branch master er idle, resten er stoppet. Mere?"
    /// </summary>
    public string ListSummary(int count, IReadOnlyList<(ListedState State, int Count, IReadOnlyList<SpokenName> Named)> states, bool more)
    {
        var counted = states.Select((s, i) => (s, i)).Where(x => x.s.Named.Count == 0).ToList();
        var rest = counted is [var only] && only.i > 0 && states.Take(only.i).All(s => s.Named.Count > 0) ? only.s.State : (ListedState?)null;
        var parts = states.Select(s =>
            s.Named.Count > 0 ? $"{And([.. s.Named.Select(Named)])} {Doing(s.State, s.Named.Count)}"
            : s.State == rest ? $"{(_danish ? "resten" : "the rest")} {Doing(s.State, 2)}"
            : $"{s.Count} {Doing(s.State, s.Count)}");
        return $"{Total(count)} {string.Join(", ", parts)}.{(more ? $" {More}" : "")}";
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
            var group = page.Skip(i).TakeWhile(p => p.State == first.State && p.Profile == first.Profile && p.Root == first.Root).ToList();
            if (first.State != state)
                sentences.Add($"{Heading(first.State)}.");
            state = first.State;
            sentences.Add(GroupLine(first.Profile, first.Root, group.Select(p => p.Label)));
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
        _danish ? $"Til {Named(before)} eller {Named(announced)}?" : $"To {Named(before)} or {Named(announced)}?";

    /// <summary>Answers went out this turn (<see cref="SentNode"/>): "Sendt til issue 283.", "Sendt til issue 283 og issue 101.".</summary>
    public string Sent(IReadOnlyList<SpokenName> names)
    {
        var to = And([.. names.Select(Named).Distinct()]);
        return _danish ? $"Sendt til {to}." : $"Sent to {to}.";
    }

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
