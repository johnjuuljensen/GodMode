using System.Globalization;
using System.Text;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Tools;

namespace GodMode.Voice;

/// <summary>
/// The graph's tools on the servers: what needs the user, which projects there are, a project's status, its last reply
/// (in parts), answer it, mark it seen, and start one (read back only: the user's yes creates it, <see cref="ConfirmCreateNode"/>). Their results are for the model, which says them in the user's language. A permission request is
/// never answered here: that is the screen's (issue #285).
/// </summary>
public sealed class VoiceTools(IGodModeServers servers, AttentionBoard board, ProjectBoard projects, ProjectHandles handles,
    VoiceConversation conversation, TimeProvider? time = null)
{
    public const string WhatNeedsMe = "what_needs_me";
    public const string ListProjects = "list_projects";
    public const string ProjectStatus = "project_status";
    public const string ReadReply = "read_reply";
    public const string ReadMore = "read_more";
    public const string Answer = "answer_project";
    public const string MarkSeen = "mark_seen";
    public const string SetImportance = "set_importance";
    public const string StartSession = "start_session";

    public const string ProjectParameter = "project";
    public const string TextParameter = "text";
    public const string RootParameter = "root";
    public const string ActionParameter = "action";
    public const string IssueParameter = "issue";
    public const string NameParameter = "name";
    public const string PromptParameter = "prompt";
    public const string TurnsParameter = "turns";
    public const string ImportanceParameter = "importance";

    /// <summary>What the conversation is about.</summary>
    public VoiceConversation Conversation => conversation;

    /// <summary>How projects are named aloud: as what they are, with their root and profile when needed (#450).</summary>
    public ProjectNames Names { get; } = new(projects, handles, conversation);

    /// <summary>The creates voice reads back, and makes on the user's yes.</summary>
    public SessionCreates Creates { get; } = new(servers, handles, time);

    /// <summary>How many projects a reference to none lists, as the options the model offers.</summary>
    private const int OptionsListed = 8;

    private static readonly ToolParameter ProjectReference = new(ProjectParameter,
        "The project as the user said it, or as a tool named it (\"issue 283\", \"branch master\", \"283\"), with the root or " +
        "profile they said it with (\"branch master i GodMode, profil Mega\"), or its root or kind when the user names it so " +
        "(\"Assistant\", \"chat\"). Leave it empty for the project last announced or talked about.", Required: false);

    public ToolSet AddTo(ToolSet tools) => tools
        .Add(WhatNeedsMe,
            "List what needs the user across all their servers: questions, permission requests, errors, reviews and " +
            "finished results, one line per project, oldest first, each named as it is said. Call when the user asks what needs them, what is waiting, or for status overall.",
            (_, _, ct) => WhatNeedsMeAsync(ct))
        .Add(ListProjects,
            "List every project on every server, whether it needs the user or not, grouped by profile, then root: each by " +
            "the name it is said by, with its name, kind and state. Call when the user asks which projects there are, what runs, or " +
            "about one they just started.",
            (_, _, _) => Task.FromResult(ListProjectsText()))
        .Add(ProjectStatus,
            "Read one project's state and what it waits on (its question, result, error or permission request) in full: " +
            "a very long one has its middle cut, and says so. " +
            "Call when the user asks about one project, or to hear a question or result.",
            [ProjectReference],
            (_, args, ct) => ProjectStatusAsync(Argument(args, ProjectParameter), ct))
        .Add(ReadReply,
            "Read what a project said last: its last reply, from its output, whether or not it needs the user, also once it " +
            "is seen or idle. A long one comes in parts, the first now, and says how many; read_more gives the next. It marks nothing seen. " +
            "Call when the user asks to hear a project's reply or answer (\"Læs hele masters svar\", \"Hvad svarede 283?\").",
            [ProjectReference, new ToolParameter(TurnsParameter,
                $"How many of its last replies to read, oldest first: 1 unless the user asked for more (\"de sidste tre svar\"), at most {MaxTurnsRead}.",
                ToolParameterType.Integer, Required: false)],
            (_, args, ct) => ReadReplyAsync(Argument(args, ProjectParameter), Argument(args, TurnsParameter), ct))
        .Add(ReadMore,
            $"Read the next part of the reply {ReadReply} read last. Call when the user says \"læs videre\", \"mere\" or \"read on\".",
            (_, _, ct) => ReadMoreAsync(ct))
        .Add(Answer,
            "Send the user's answer to a project: it reaches the Claude session as the user's reply, and the session " +
            "continues. Give the answer as the instruction the user meant, in their words.",
            [new ToolParameter(TextParameter, "The answer to send, e.g. \"Brug den eksisterende migration.\""), ProjectReference],
            (_, args, ct) => AnswerAsync(Argument(args, ProjectParameter), Argument(args, TextParameter), ct))
        .Add(MarkSeen,
            "Mark what a project needs as seen (its finished result, an error, or a question it asked in plain text), so it no " +
            "longer needs the user; a seen question still waits for its answer. A pending choice or permission is only cleared by " +
            "an answer. Call only when the user says so themselves (\"læst\", \"seen\"): never as part of reading a project, its status or its reply, nor when they ask whether that was all.",
            [ProjectReference],
            (_, args, ct) => MarkSeenAsync(Argument(args, ProjectParameter), ct))
        .Add(SetImportance,
            "Set how much a project may interrupt the user: \"important\" (a sound, and said first), \"normal\", or \"quiet\" " +
            "(its results and errors stay in the inbox; its questions still reach the user). Call only when the user asks " +
            "(\"marker [handle] som vigtig\", \"som normal\", \"som stille\").",
            [new ToolParameter(ImportanceParameter, "\"important\", \"normal\" or \"quiet\"."), ProjectReference],
            (_, args, ct) => SetImportanceAsync(Argument(args, ProjectParameter), Argument(args, ImportanceParameter), ct))
        .Add(StartSession,
            "Prepare a new session (project) in a root: an issue (\"Start issue 283\", \"Start sag BD-123 i api\") or a chat, " +
            "experiment or other session with a name and a prompt (\"Start en chat i Assistant om backup-jobbet\"). It creates " +
            "nothing: it says what to read back, or what to ask. Only the user's own yes to the read-back creates it; never say it was created.",
            [
                new ToolParameter(RootParameter, "The root or profile as the user named it (\"GodMode\", \"assistenten\", \"kappe\"). Empty when they named none: never guess one.", Required: false),
                new ToolParameter(ActionParameter, "The kind of session as the user named it (\"issue\", \"chat\", \"experiment\"), or empty.", Required: false),
                new ToolParameter(IssueParameter, "The issue's number or key as said (\"283\", \"BD-123\"), or empty.", Required: false),
                new ToolParameter(NameParameter, "A short name for the session, from what the user said it is about (\"backup job\"), or empty for an issue.", Required: false),
                new ToolParameter(PromptParameter, "What the session should do, in the user's words, or empty when they said nothing more.", Required: false),
            ],
            (_, args, ct) => StartSessionAsync(new CreateAsk(Argument(args, RootParameter), Argument(args, ActionParameter),
                Argument(args, IssueParameter), Argument(args, NameParameter), Argument(args, PromptParameter)), ct));

    public async Task<string> WhatNeedsMeAsync(CancellationToken ct)
    {
        // Those of projects voice knows: an item of one it has not heard of (yet, or any more) has no handle to say
        var items = (await servers.GetAttentionAsync(ct)).Where(i => handles.Of(i.Project) is not null).ToList();
        // What was read out is what the conversation is about now: one project, or none to answer unnamed
        Talked(items is [var only] ? only.Project : null);
        if (items.Count == 0)
            return "Nothing needs the user.";

        var text = new StringBuilder($"{items.Count} need the user:\n");
        var named = items.Select(i => (Item: i, Name: Names.Of(i.Project)!)).ToList();
        foreach (var (item, name) in named)
            text.AppendLine($"- {name}: {Describe(item.Item)}{InItsWords(item.Item)}");
        // One project, with its own spoken reply: the system says it, as status would
        if (named is [{ Item.Item.Spoken.Length: > 0 } one])
            text.AppendLine(SpokenBySystem(one.Name, one.Item.Item));
        return ReadOut(text.ToString().TrimEnd());
    }

    public string ListProjectsText()
    {
        var all = projects.Projects;
        // As what needs me: one project read out is the one talked about, several leave none
        Talked(all is [var only] ? only.Ref : null);
        if (all.Count == 0)
            return "No projects on any server.";

        // Grouped by profile, then root, each said once (#450): a session's label alone ("branch master") says nothing of where it is
        var groups = Names.Groups();
        var text = new StringBuilder(groups is [_]
            ? $"{Count(all.Count)}, all in one group:\n"
            : $"{Count(all.Count)}, in {groups.Count} groups by profile and root:\n");
        foreach (var group in groups)
        {
            text.Append($"{group.Heading} ({Count(group.Projects.Count)}):\n");
            foreach (var project in group.Projects)
                text.Append($"- {handles.LabelOf(project.Ref) ?? project.Project.Name} ({Details(project.Project.Name, project.Project.Kind)}): {project.Project.State}\n");
        }
        text.Append($"Say the count, {all.Count}, then each group once, by its profile and root, with its projects by the names given here. " +
            "If you leave any out, say how many and why.");
        return text.ToString();
    }

    private static string Count(int projects) => projects == 1 ? "1 project" : $"{projects} projects";

    public async Task<string> ProjectStatusAsync(string? reference, CancellationToken ct)
    {
        if (Target(reference) is not { } target || Names.Of(target) is not { } name)
            return await UnknownAsync(reference, ct);

        var status = await servers.GetStatusAsync(target, ct);
        Talked(target);
        var text = new StringBuilder($"{name} ({Details(status.Name, status.Kind)}): {status.State}.");
        var item = board.ItemOf(target)?.Item;
        text.Append(Standing(status, item));
        if (item is not null)
        {
            text.Append($" Needs the user: {Describe(item, InFull(item, status))}");
            if (item.Spoken is { Length: > 0 })
                text.Append(' ').Append(SpokenBySystem(name, item));
        }
        else if (status.CurrentQuestion is { Length: > 0 } question)
            text.Append($" Asked: {Capped(question)}");
        if (status.LastError is { Length: > 0 } error && status.State == ProjectState.Error && item?.Kind != AttentionKind.Error)
            text.Append($" Error: {Capped(error)}");
        return ReadOut(text.ToString());
    }

    /// <summary>How much of a last result <see cref="Standing"/> reads when the session gave no line of its own: its start, a few sentences.</summary>
    public const int StandingResultLength = 300;

    /// <summary>
    /// Where the project stands (issue #466), in this order: the recap the session keeps of it
    /// (<see cref="ProjectStatus.Recap"/>), else, with no attention item to read the turn from, its last spoken reply, else
    /// its last result shortened. Nothing when it has none of them.
    /// </summary>
    private static string Standing(ProjectStatus status, AttentionItem? item) => status switch
    {
        { Recap: { Length: > 0 } recap } => $" Where it stands, in its own words: \"{recap}\"",
        _ when item is not null => "",
        { SpokenSummary: { Length: > 0 } spoken } => $" Its last reply, in its own spoken words: \"{spoken}\"",
        { LastResult: { Length: > 0 } result } => $" Its last result: {Shortened(result.Trim())}",
        _ => "",
    };

    /// <summary>The text, or its start to a word at about <see cref="StandingResultLength"/> characters with "…".</summary>
    private static string Shortened(string text) =>
        text.Length <= StandingResultLength ? text
            : text[..(text.LastIndexOf(' ', StandingResultLength) is var at and > 0 ? at : Whole(text, StandingResultLength))] + "…";

    /// <summary>A tool's text, which reads out a project's own words, for the model to say: kept for <see cref="SentNode"/>.</summary>
    private string ReadOut(string text)
    {
        conversation.ReadOut(text);
        return text;
    }

    /// <summary>The session's own spoken reply, word for word, after a line's text; nothing when it gave none.</summary>
    private static string InItsWords(AttentionItem item) =>
        item.Spoken is { Length: > 0 } spoken ? $" In its own spoken words: \"{spoken}\"" : "";

    /// <summary>
    /// The item's spoken reply goes to the system (<see cref="SpokenNode"/>), which says it word for word in place of the
    /// model's reply (issue #384), and what the tool's result tells the model of it.
    /// </summary>
    private string SpokenBySystem(SpokenName name, AttentionItem item)
    {
        conversation.Spoke(name, item);
        return $"Its own spoken reply, \"{item.Spoken}\", is said word for word by the system itself, in place of your reply: respond with one word.";
    }

    /// <summary>
    /// About how long a question, result or error <see cref="ProjectStatusAsync"/> reads may be (issue #377): far over
    /// the attention item's 500 characters, so a reply of a few thousand characters is read whole, but short of one
    /// that would fill the model's turn and every turn after it in the conversation.
    /// </summary>
    public const int MaxStatusTextLength = 8000;

    /// <summary>How much of a text over <see cref="MaxStatusTextLength"/> is kept from its start; the rest is its end, where a reply's question is.</summary>
    private const int KeptFromStart = 2000;

    /// <summary>
    /// What the item is about in full, from the project's status: the attention item's text is cut for lists and
    /// notifications, and a reply's question is at its end.
    /// </summary>
    private static string InFull(AttentionItem item, ProjectStatus status) => Capped(item.Kind switch
    {
        AttentionKind.Question when status.PendingQuestion is { Questions: [_, ..] questions } =>
            string.Join("\n", questions.Select(q => q.Question)),
        AttentionKind.Question when status.CurrentQuestion is { Length: > 0 } question => question,
        AttentionKind.Finished when status.LastResult is { Length: > 0 } result => result,
        AttentionKind.Error when status.LastError is { Length: > 0 } error => error,
        _ => item.Text,
    });

    /// <summary>The text trimmed, or, over <see cref="MaxStatusTextLength"/>, its start and end with the cut said where its middle was.</summary>
    private static string Capped(string text)
    {
        text = text.Trim();
        if (text.Length <= MaxStatusTextLength)
            return text;

        var head = Whole(text, KeptFromStart);
        var tail = Whole(text, text.Length - (MaxStatusTextLength - KeptFromStart));
        return string.Create(CultureInfo.InvariantCulture, $"{text[..head]} [... {tail - head} characters cut here; the start and the end are read in full ...] {text[tail..]}");
    }

    /// <summary>The index <paramref name="length"/>, or one before it where a cut there would split a surrogate pair.</summary>
    private static int Whole(string text, int length) =>
        length > 0 && length < text.Length && char.IsLowSurrogate(text[length]) ? length - 1 : length;

    /// <summary>
    /// About how long a part of a reply <see cref="ReadReplyAsync"/> and <see cref="ReadMoreAsync"/> give is: what the model
    /// says in one go, a minute or so of speech. The whole reply read is <see cref="Capped"/>, as a status's text is.
    /// </summary>
    public const int ReplyPartLength = 1200;

    /// <summary>The most replies <see cref="ReadReplyAsync"/> reads at once: the last few, well under the hub's <see cref="Shared.Hubs.IProjectHub.MaxReplyTurns"/>.</summary>
    public const int MaxTurnsRead = 5;

    /// <summary>
    /// The project's last reply, or its last <paramref name="turns"/> (1 when none or not a number, at most
    /// <see cref="MaxTurnsRead"/>), from its output on the server, whatever it waits on (issue #378). A reply longer than
    /// <see cref="ReplyPartLength"/> gives its first part, and is kept for <see cref="ReadMoreAsync"/>. Nothing is marked seen.
    /// </summary>
    public async Task<string> ReadReplyAsync(string? reference, string? turns, CancellationToken ct)
    {
        if (Target(reference) is not { } target || Names.Of(target) is not { } name)
            return await UnknownAsync(reference, ct);

        var count = int.TryParse(turns, NumberStyles.Integer, CultureInfo.InvariantCulture, out var asked) && asked >= 1 ? Math.Min(asked, MaxTurnsRead) : 1;
        var status = await servers.GetStatusAsync(target, ct);
        var replies = await servers.GetLastRepliesAsync(target, count, ct);
        Talked(target);
        var header = $"{name} ({Details(status.Name, status.Kind)}): {status.State}.";
        if (replies.Count == 0)
        {
            conversation.Reading = null;
            return $"{header} It has said nothing yet.";
        }

        var said = replies is [var one]
            ? $"Last reply{Flags(one)}: {one.Text.Trim()}"
            : $"Last {replies.Count} replies, oldest first:\n" + string.Join("\n", replies.Select((r, i) => $"Reply {i + 1}{Flags(r)}: {r.Text.Trim()}"));
        var parts = Parts(Capped(said));
        conversation.Reading = new ReplyReading(target, name.ToString(), parts, 1, count, replies);
        return ReadOut(parts.Count == 1
            ? $"{header} {parts[0]}"
            : $"{header} {parts[0]} [Part 1 of {parts.Count}: more follows; {ReadMore} reads it.]");
    }

    /// <summary>
    /// The next part of the reply <see cref="ReadReplyAsync"/> read last, or that there is none. A project that has
    /// written since (a new reply, or more of one it was working on) has its old one dropped, and says so (#411):
    /// "læs videre" never reads on in a reply that is no longer its last.
    /// </summary>
    public async Task<string> ReadMoreAsync(CancellationToken ct)
    {
        if (conversation.Reading is not { } reading || reading.Next >= reading.Parts.Count)
            return $"Nothing more to read: the last reply read was read to its end. {ReadReply} reads a project's reply.";

        Talked(reading.Project);
        if (!(await servers.GetLastRepliesAsync(reading.Project, reading.Turns, ct)).SequenceEqual(reading.Replies))
        {
            conversation.Reading = null;
            return $"{reading.Handle} has written a new reply since the one being read, so the rest of that one is not read: " +
                $"say so, and offer to read the new one with {ReadReply}.";
        }

        conversation.Reading = reading with { Next = reading.Next + 1 };
        var last = reading.Next + 1 == reading.Parts.Count;
        return ReadOut($"{reading.Handle}'s reply, part {reading.Next + 1} of {reading.Parts.Count}: {reading.Parts[reading.Next]}" +
            (last ? " [That was the end of it.]" : $" [More follows: {ReadMore} reads it.]"));
    }

    /// <summary>What a reply was besides its text: a failure, or a turn claude is still on (or was stopped in).</summary>
    private static string Flags(AssistantReply reply) =>
        reply.IsError ? " (failed)" : reply.Finished ? "" : " (unfinished: it is still working on it)";

    /// <summary>
    /// The text in parts of at most <see cref="ReplyPartLength"/>, each ended where it reads well: at a paragraph, else a
    /// sentence, in its second half, else at a space. Joined with a space, they are the text, but for the whitespace at the cuts.
    /// </summary>
    private static IReadOnlyList<string> Parts(string text)
    {
        var parts = new List<string>();
        var rest = text;
        while (rest.Length > ReplyPartLength)
        {
            var cut = Break(rest);
            parts.Add(rest[..cut].TrimEnd());
            rest = rest[cut..].TrimStart();
        }
        if (rest.Length > 0)
            parts.Add(rest);
        return parts;
    }

    /// <summary>Where the first part of <paramref name="text"/>, longer than <see cref="ReplyPartLength"/>, ends.</summary>
    private static int Break(string text)
    {
        var window = text[..(ReplyPartLength + 1)];
        var half = ReplyPartLength / 2;
        if (window.LastIndexOf("\n\n", StringComparison.Ordinal) is var paragraph and >= 0 && paragraph >= half)
            return paragraph;
        for (var i = ReplyPartLength - 1; i >= half; i--)
            if (window[i] is '.' or '?' or '!' or ':' && char.IsWhiteSpace(window[i + 1]))
                return i + 1;
        return window.LastIndexOfAny([' ', '\n', '\t']) is var space and > 0 ? space : Whole(text, ReplyPartLength);
    }

    public async Task<string> AnswerAsync(string? reference, string? answer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return "No answer given: ask the user what to answer.";
        if (Target(reference) is not { } target || Names.Of(target) is not { } name)
            return await UnknownAsync(reference, ct);

        var status = await servers.GetStatusAsync(target, ct);
        if (status.CreateFailed)
        {
            conversation.Current = target;
            return $"{name} failed to create, so it has no session to answer. " +
                "Nothing was sent: tell the user to delete it, or create it again.";
        }
        if (status.PendingPermission is { } permission)
        {
            conversation.Current = target;
            return $"{name} is waiting on a permission request ({permission.Summary}), which is answered on screen, " +
                "not by voice. Nothing was sent: tell the user to answer it on screen.";
        }

        await servers.ReplyAsync(target, answer.Trim(), ct);
        conversation.Current = target;
        conversation.Sent(name);
        return $"Sent to {name}: \"{answer.Trim()}\". It continues. The system says it was sent itself.";
    }

    public async Task<string> MarkSeenAsync(string? reference, CancellationToken ct)
    {
        if (Target(reference) is not { } target)
            return await UnknownAsync(reference, ct);

        await servers.MarkSeenAsync(target, ct);
        conversation.Current = target;
        return $"{Names.Of(target)?.ToString() ?? target.ProjectId} is marked seen.";
    }

    public async Task<string> SetImportanceAsync(string? reference, string? importance, CancellationToken ct)
    {
        if (ParseImportance(importance) is not { } tier)
            return $"\"{importance}\" is no importance: say important, normal or quiet. Nothing was changed.";
        if (Target(reference) is not { } target)
            return await UnknownAsync(reference, ct);

        await servers.SetImportanceAsync(target, tier, ct);
        conversation.Current = target;
        return $"{Names.Of(target)?.ToString() ?? target.ProjectId} is now {tier.ToString().ToLowerInvariant()}.";
    }

    /// <summary>The tier the model named, in English or Danish as the user said it; null for anything else.</summary>
    internal static Importance? ParseImportance(string? importance) => importance?.Trim().ToLowerInvariant() switch
    {
        "important" or "vigtig" => Importance.Important,
        "normal" => Importance.Normal,
        "quiet" or "stille" => Importance.Quiet,
        _ => null,
    };

    /// <summary>What the conversation is about from now on, from a tool that read it out.</summary>
    private void Talked(ProjectRef? project) => conversation.Current = project;

    /// <summary>What to read back for a create, or ask, or why there is none (<see cref="SessionCreates.Propose"/>).</summary>
    public async Task<string> StartSessionAsync(CreateAsk ask, CancellationToken ct) =>
        await Creates.ProposeAsync(await servers.ListRootsAsync(ct), ask, ct);

    /// <summary>
    /// The project named, or the one the conversation is about when none is named, while it is still there. Every
    /// project the servers have has a handle already (<see cref="ProjectBoard"/>), whether it has needed the user or not.
    /// </summary>
    private ProjectRef? Target(string? reference) =>
        string.IsNullOrWhiteSpace(reference)
            ? conversation.Current is { } current && handles.Of(current) is not null ? current : null
            : handles.Resolve(reference);

    private async Task<string> UnknownAsync(string? reference, CancellationToken ct)
    {
        var waiting = (await servers.GetAttentionAsync(ct)).Select(i => Names.Full(i.Project)).OfType<SpokenName>().ToList();
        var which = waiting.Count > 0 ? $" Waiting now: {string.Join("; ", waiting)}." : " Nothing needs the user now.";
        var all = projects.Projects;
        var options = all.Count == 0 ? " No projects on any server."
            : $" Projects: {string.Join("; ", all.Take(OptionsListed).Select(Line))}{(all.Count > OptionsListed ? $"; {all.Count - OptionsListed} more" : "")}.";
        // A label several projects have ("branch master" in two profiles) names none of them: say which there are
        var several = string.IsNullOrWhiteSpace(reference) ? [] : handles.Labelled(reference);
        return string.IsNullOrWhiteSpace(reference)
            ? $"No project is being talked about: ask the user which one.{which}{options}"
            : several.Count > 1
                ? $"'{reference}' names {several.Count} projects: {string.Join("; ", several.Select(Names.Full).OfType<SpokenName>())}. " +
                    "Nothing was done: ask which, as a closed question naming each by its root and profile."
            : handles.IsRetired(reference)
                ? $"Unknown project '{reference}': that project was deleted. Nothing was done.{which}{options}"
                : $"Unknown project '{reference}'.{which}{options}";
    }

    /// <summary>"chat testing in Assistant, profile Outbound (testing, chat): Idle", its root and profile when there are several.</summary>
    private string Line(ServerProject project)
    {
        var p = project.Project;
        return $"{Names.Full(project.Ref)?.ToString() ?? p.Name} ({Details(p.Name, p.Kind)}): {p.State}";
    }

    /// <summary>The project's name and kind, those it has.</summary>
    private static string Details(string name, string? kind) =>
        string.Join(", ", new[] { name, kind }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>The item as a line says it, with <paramref name="text"/> for its text: the item's own (cut) one when null.</summary>
    private static string Describe(AttentionItem item, string? text = null)
    {
        var said = text ?? item.Text;
        return item.Kind switch
        {
            AttentionKind.Question => $"question: {said}",
            AttentionKind.Permission => $"permission request ({item.Permission?.Summary ?? said}); answered on screen only",
            AttentionKind.Error => $"failed: {said}",
            AttentionKind.Escalation => $"needs the user's decision: {said}",
            AttentionKind.Review => $"changes requested on its pull request: {said}",
            AttentionKind.Finished => $"finished: {said}",
        };
    }

    private static string? Argument(IDictionary<string, object?> args, string name) =>
        args.TryGetValue(name, out var value) ? value?.ToString() : null;
}
