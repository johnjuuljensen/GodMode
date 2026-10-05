using System.Globalization;
using System.Text;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Tools;

namespace GodMode.Voice;

/// <summary>
/// The graph's tools on the servers: what needs the user, which projects there are, a project's status, its last reply
/// (in parts), answer it, mark it seen, and start one (read back only: the user's yes creates it, <see cref="ConfirmCreateNode"/>). Their results are for the model, which says them in the user's language. A permission request is
/// never answered here: that is the screen's (issue #285). What needs the user, the projects, and a project's question
/// or result short enough to say as it is are said in the code's words (<paramref name="phrases"/>), and the model does
/// not retell them (<see cref="VoiceConversation.SaysItself"/>, #456).
/// </summary>
/// <param name="staleAfter">
/// How long without activity leaves a session out of a list unless the user asks for all (#468,
/// <see cref="VoiceSettings.StaleHours"/>); the default's when null.
/// </param>
public sealed class VoiceTools(IGodModeServers servers, AttentionBoard board, ProjectBoard projects, ProjectHandles handles,
    VoiceConversation conversation, TimeProvider? time = null, VoicePhrases? phrases = null, TimeSpan? staleAfter = null)
{
    private readonly VoicePhrases _phrases = phrases ?? new VoicePhrases(VoiceSettings.Default.Languages);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _staleAfter = staleAfter ?? VoiceSettings.Default.StaleAfter;

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
    public const string SinceParameter = "since";
    public const string WorkersParameter = "workers";
    public const string ThenParameter = "then";

    /// <summary>The <see cref="WorkersParameter"/> that takes in every overseer's workers (#469).</summary>
    public const string WorkersAll = "all";

    /// <summary>The <see cref="WorkersParameter"/> for the workers of the overseer talked about ("overseerens workers").</summary>
    public const string WorkersCurrent = "current";

    /// <summary>The <see cref="SinceParameter"/> that lists every project, stale ones too.</summary>
    public const string SinceAll = "all";

    /// <summary>The <see cref="SinceParameter"/> that lists what happened since this voice session last listed.</summary>
    public const string SinceLastAsked = "last";

    /// <summary>What the conversation is about.</summary>
    public VoiceConversation Conversation => conversation;

    /// <summary>How projects are named aloud: as what they are, with their root and profile when needed (#450).</summary>
    public ProjectNames Names { get; } = new(projects, handles, conversation);

    /// <summary>The creates voice reads back, and makes on the user's yes.</summary>
    public SessionCreates Creates { get; } = new(servers, handles, time);

    /// <summary>The dictation being taken, sent word for word on the user's "send" (#459, <see cref="DictationNode"/>).</summary>
    public Dictation Dictation => field ??= new(servers, handles, Names, conversation, _phrases, _time);

    /// <summary>How many projects a reference to none lists, as the options the model offers.</summary>
    private const int OptionsListed = 8;

    private static readonly ToolParameter ProjectReference = new(ProjectParameter,
        "The project as the user said it, or as a tool named it (\"issue 283\", \"branch master\", \"283\"), with the root or " +
        "profile they said it with (\"branch master i GodMode, profil Mega\"), or its root or kind when the user names it so " +
        "(\"Assistant\", \"chat\"). Leave it empty for the project last announced or talked about.", Required: false);

    private static readonly ToolParameter InRoot = new(RootParameter,
        "Only those of one root or profile, as the user named it (\"i GodMode\", \"i Mega\"), when they asked about one. " +
        "Empty for all of them.", Required: false);

    /// <summary>The window a list takes in (#468), as the model gives it from what the user said.</summary>
    private ToolParameter Since => new(SinceParameter,
        $"Which projects to take in. Empty for the default: those active in the last {StaleHoursSaid} hours, and every one that " +
        $"needs the user; the stale rest are counted, not named. \"{SinceAll}\" when the user asks for all of them or the old " +
        $"ones too (\"alle\", \"også de gamle\"). \"{SinceLastAsked}\" for what happened since they last asked (\"siden jeg " +
        "sidst spurgte\"). A number of minutes for a time window they named (\"den sidste time\": 60).", Required: false);

    /// <summary>Which of the sessions overseers run a list takes in (#469), as the model gives it from what the user said.</summary>
    private static readonly ToolParameter Workers = new(WorkersParameter,
        "Sessions an overseer runs (its workers) are left out, and counted on their overseer's line: the overseer handles " +
        $"them. Empty for that. \"{WorkersAll}\" when the user asks for every worker too (\"alle\", \"også workers\"). An " +
        "overseer as the user named it, for its workers alone (\"hvad laver voice-epics' workers\": \"voice-epics\"); " +
        $"\"{WorkersCurrent}\" for those of the overseer talked about (\"overseerens workers\"). A worker the user names by its " +
        $"handle needs none of this: {ProjectStatus} reads it.", Required: false);

    /// <summary>
    /// What the user asked for after this call, in the same breath (#507): the code does not end the turn on its words for
    /// it (<see cref="CodeSaysInference"/>), and the model makes the next call.
    /// </summary>
    private static readonly ToolParameter Then = new(ThenParameter,
        "What the user asked for after this, in the same breath, which another call does (\"læs 283\" in \"hvad venter, og " +
        "læs 283\"). Empty when they asked for nothing more.", Required: false);

    /// <summary>The tool's result, which the code says after the rest the user asked for (<see cref="Then"/>), when they asked for more.</summary>
    private async Task<string> AndThen(IDictionary<string, object?> args, Task<string> call)
    {
        var result = await call;
        if (Argument(args, ThenParameter) is { } then && !string.IsNullOrWhiteSpace(then))
            conversation.Then(result, then.Trim());
        return result;
    }

    private string StaleHoursSaid => _staleAfter.TotalHours.ToString("0.#", CultureInfo.InvariantCulture);

    public ToolSet AddTo(ToolSet tools) => tools
        .Add(WhatNeedsMe,
            "List what needs the user across all their servers: questions, permission requests, errors, reviews and " +
            "finished results, one line per project, the most recent first, each named as it is said; an overseer's workers " +
            "on its own line, counted, unless the user asks for them. Call when the user asks what needs them, what is waiting, or for status overall.",
            [InRoot, Since, Workers, Then],
            (_, args, ct) => AndThen(args, WhatNeedsMeAsync(ct, Argument(args, RootParameter), Argument(args, SinceParameter), Argument(args, WorkersParameter))))
        .Add(ListProjects,
            "List the projects on every server, whether they need the user or not, the most recent first, grouped by profile, " +
            "then root: each by the name it is said by, with its name, kind and state. Stale ones are left out and counted " +
            "unless the user asks for all; an overseer's workers are counted on its line, unless the user asks for them. Call " +
            "when the user asks which projects there are, what runs, what has happened, or about one they just started.",
            [InRoot, Since, Workers, Then],
            (_, args, _) => AndThen(args, Task.FromResult(ListProjectsText(Argument(args, RootParameter), Argument(args, SinceParameter), Argument(args, WorkersParameter)))))
        .Add(ProjectStatus,
            "Read one project's state and what it waits on (its question, result, error or permission request) in full: " +
            "a very long one has its middle cut, and says so. " +
            "Call when the user asks about one project, or to hear a question or result.",
            [ProjectReference, Then],
            (_, args, ct) => AndThen(args, ProjectStatusAsync(Argument(args, ProjectParameter), ct)))
        .Add(ReadReply,
            "Read what a project said last: its last reply, from its output, whether or not it needs the user, also once it " +
            "is seen or idle. A long one comes in parts, the first now, and says how many; read_more gives the next. It marks nothing seen. " +
            "Call when the user asks to hear a project's reply or answer (\"Læs hele masters svar\", \"Hvad svarede 283?\").",
            [ProjectReference, new ToolParameter(TurnsParameter,
                $"How many of its last replies to read, oldest first: 1 unless the user asked for more (\"de sidste tre svar\"), at most {MaxTurnsRead}.",
                ToolParameterType.Integer, Required: false)],
            (_, args, ct) => ReadReplyAsync(Argument(args, ProjectParameter), Argument(args, TurnsParameter), ct))
        .Add(ReadMore,
            $"Read more of what was said last: the next part of the reply {ReadReply} read, or of the long list {ListProjects} said; " +
            "or, after a line about one project (its announcement, or its status), that line a step further: an announcement " +
            "into the project's status (its question, result or error), a status into its last reply. Call when the user says " +
            "\"læs videre\", \"mere\" or \"read on\", or asks about what was just said: \"Hvorfor?\", \"Hvad er det?\", " +
            "\"Mere?\" / \"Why?\", \"What is it?\".",
            [Then],
            (_, args, ct) => AndThen(args, ReadMoreAsync(ct)))
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

    /// <param name="root">Only the items of the projects in this root or profile (<see cref="In"/>); all when empty.</param>
    /// <param name="since">
    /// The window the user asked for (<see cref="Since"/>, #468), the most recent first in any: what needs the user is never
    /// stale, so only a window they named leaves any out, and counts them.
    /// </param>
    /// <param name="workers">
    /// Which workers it takes in (<see cref="Workers"/>, #469): by default an item of a session a live overseer runs is not
    /// named, and its overseer has a line that counts it.
    /// </param>
    public async Task<string> WhatNeedsMeAsync(CancellationToken ct, string? root = null, string? since = null, string? workers = null)
    {
        var now = Now;
        if (AskOf(since, now, root) is not { } ask)
            return NoSuchWindow(since!);
        if (ScopeOf(workers, since) is not { } scope)
            return NoSuchOverseer(workers!);
        // Those of projects voice knows: an item of one it has not heard of (yet, or any more) has no handle to say
        var all = (await servers.GetAttentionAsync(ct)).Where(i => handles.Of(i.Project) is not null).ToList();
        if (!string.IsNullOrWhiteSpace(root))
        {
            if (!projects.Projects.Any(p => In(p, root)))
                return NoSuchRoot(root);
            // A worker's item goes with its overseer's line, so its overseer's root decides
            all = [.. all.Where(i => projects.Find(i.Project) is { } p && In(scope.Shown && projects.Holds(i) ? projects.TopOf(p) : p, root))];
        }
        Listed(root, scope, now);
        DateTime ActivityOfItem(ServerAttentionItem i) => projects.Find(i.Project) is { } p ? ProjectListing.ActivityOf(p.Project, now) : i.Item.Since;
        // Unasked, workers' are held back (#469), but for a nested overseer's escalation: each overseer's line counts those its
        // workers have, in the window
        var (items, left) = ProjectListing.Within(all.Where(i => scope.Shown ? !projects.Holds(i) : scope.Takes(i.Project)), ask.Window, ActivityOfItem, _ => true);
        var (held, _) = ProjectListing.Within(all.Where(i => scope.Shown && projects.Holds(i)), ask.Window, ActivityOfItem, _ => true);
        // Named in the order they are said (#455): the items, then the overseers' lines
        var named = items.Select(i => (Item: i, Name: Names.Of(i.Project)!)).ToList();
        var overseers = held.Select(i => projects.Find(i.Project)).OfType<ServerProject>().GroupBy(p => projects.TopOf(p).Ref)
            .Select(g => (Overseer: g.Key, Name: Names.Of(g.Key), Workers: projects.WorkersOf(g.Key).Count, Waiting: g.Count()))
            .Where(o => o.Name is not null).Select(o => (o.Overseer, Name: o.Name!, o.Workers, o.Waiting)).ToList();
        // What was read out is what the conversation is about now: one project, or none to answer unnamed
        var alone = (items, overseers) switch
        {
            ([var only], []) => only.Project,
            ([], [var overseer]) => overseer.Overseer,
            _ => null,
        };
        Talked(alone);
        // One project said alone is the last line, which "Mere?" expands into its status (#455)
        if (alone is not null)
            conversation.Reading = new ProjectLine(alone, Read: false);
        if (items.Count == 0 && overseers.Count == 0)
        {
            // Nothing read out: nothing for "mere" to read on in, whatever was before (#507)
            conversation.Reading = null;
            return left.Count > 0
                ? SaysItself($"Nothing that needs the user has had activity {ask.Said}; {left.Count} need the user from before, not named.{ask.Note}",
                    _phrases.WaitingFromBefore(left.Count, saidAny: false))
                : SaysItself((string.IsNullOrWhiteSpace(root) ? "Nothing needs the user." : $"Nothing in {root} needs the user.") + ask.Note, _phrases.Waiting([]));
        }

        var text = new StringBuilder(items.Count > 0 ? $"{items.Count} need the user:\n" : "Nothing needs the user directly:\n");
        foreach (var (item, name) in named)
            text.AppendLine($"- {name}: {Describe(item.Item)}{InItsWords(item.Item)}");
        foreach (var overseer in overseers)
            text.AppendLine($"- {overseer.Name}: runs {OverseerLine(overseer.Workers, overseer.Waiting)}");
        if (overseers.Count > 0)
            text.AppendLine($"Workers' items are their overseer's to handle, and not named. If the user asks for them, call {WhatNeedsMe} " +
                $"with {WorkersParameter} the overseer, or \"{WorkersAll}\".");
        if (left.Count > 0)
            text.AppendLine($"{left.Count} more need the user from before, with no activity {ask.Said}: not named.");
        if (ask.Note.Length > 0)
            text.AppendLine(ask.Note.Trim());
        // One project, with its own spoken reply: the system says it, as status would
        if (named is [{ Item.Item.Spoken.Length: > 0 } one])
            text.AppendLine(SpokenBySystem(one.Name, one.Item.Item));
        var waiting = _phrases.Waiting([.. named.Select(n => (n.Name, n.Item.Item))], [.. overseers.Select(o => (o.Name, o.Workers, o.Waiting))]);
        return SaysItself(ReadOut(text.ToString().TrimEnd()),
            left.Count > 0 ? $"{waiting} {_phrases.WaitingFromBefore(left.Count, saidAny: true)}" : waiting);
    }

    /// <param name="root">Only the projects in this root or profile (<see cref="In"/>); all when empty.</param>
    /// <param name="since">
    /// The window (<see cref="Since"/>, #468): by default the stale projects are left out, but for those that need the
    /// user, and counted; <see cref="SinceAll"/> takes in all; a window the user named takes in those with activity in it.
    /// </param>
    /// <param name="workers">
    /// Which workers it takes in (<see cref="Workers"/>, #469): by default a session a live overseer runs is left out, and
    /// its overseer's line counts it; its overseer's activity, and whether it needs the user, take its workers' in.
    /// </param>
    public string ListProjectsText(string? root = null, string? since = null, string? workers = null)
    {
        var now = Now;
        if (AskOf(since, now, root) is not { } ask)
            return NoSuchWindow(since!);
        if (ScopeOf(workers, since) is not { } scope)
            return NoSuchOverseer(workers!);
        var filtered = !string.IsNullOrWhiteSpace(root);
        var all = filtered ? [.. scope.Projects.Where(p => In(p, root!))] : scope.Projects;
        if (filtered && all.Count == 0 && scope.Overseer is null)
            return NoSuchRoot(root!);
        Listed(root, scope, now);
        // Unasked, an overseer stands for its workers: it is as recent as the last of them, and needs the user when one does
        IReadOnlyList<ServerProject> Stands(ServerProject p) => scope.Shown ? [p, .. projects.WorkersOf(p.Ref)] : [p];
        DateTime Activity(ServerProject p) => Stands(p).Max(s => ActivityOf(s, now));
        var (kept, left) = ProjectListing.Within(all, ask.Window, Activity, p => Stands(p).Any(s => StateOf(s) == ListedState.NeedsYou));
        // As what needs me: one project read out is the one talked about, several leave none
        Talked(kept is [var only] ? only.Ref : null);
        if (all.Count == 0)
        {
            conversation.Reading = null;
            return scope.Overseer is { } none
                ? SaysItself($"{Names.Of(none)} runs no workers{(filtered ? $" in {root}" : "")} now.", _phrases.Projects([]))
                : SaysItself("No projects on any server.", _phrases.Projects([]));
        }
        if (kept.Count == 0)
        {
            conversation.Reading = null;
            return SaysItself($"No project has had activity {ask.Said}. {LeftOutText(left, ask)}{ask.Note}", _phrases.NothingNew(left));
        }

        // Grouped by profile, then root, each said once (#450): a session's label alone ("branch master") says nothing
        // of where it is. The group with the most recent first, and the most recent first in each (#468)
        var groups = Names.Groups()
            .Select(g => g with { Projects = [.. g.Projects.Where(kept.Contains).OrderByDescending(Activity)] })
            .Where(g => g.Projects.Count > 0)
            .OrderByDescending(g => Activity(g.Projects[0]))
            .ToList();
        // An overseer's line counts its workers (#469), unless they are listed themselves
        // Each with its topic (#507), as a list says it
        string ListLabelOf(ServerProject p) => _phrases.Listed(new SpokenName(LabelOf(p), Topic: Names.Full(p.Ref)?.Topic))
            + (scope.Shown && projects.WorkersOf(p.Ref) is { Count: > 0 } run
                ? $" ({_phrases.RunsWorkers(run.Count, run.Count(w => StateOf(w) == ListedState.NeedsYou))})"
                : "");
        var text = new StringBuilder(scope.Overseer is { } of ? $"The workers {Names.Of(of)} runs: " : "");
        text.Append(groups is [_]
            ? $"{Count(kept.Count)}, all in one group:\n"
            : $"{Count(kept.Count)}, in {groups.Count} groups by profile and root:\n");
        foreach (var group in groups)
        {
            text.Append($"{group.Heading} ({Count(group.Projects.Count)}):\n");
            foreach (var project in group.Projects)
                text.Append($"- {LabelOf(project)} ({Details(project.Project.Name, project.Project.Kind)}): {project.Project.State}{ListedLine(project, scope)}\n");
        }
        if (left.Count > 0)
            text.Append(LeftOutText(left, ask)).Append(' ');
        if (ask.Note.Length > 0)
            text.Append(ask.Note.Trim()).Append(' ');
        if (kept.Count <= ProjectListing.Page)
        {
            // Said whole: nothing is left for "mere" to read on in
            conversation.Reading = null;
            text.Append($"Say the count, {kept.Count}, then each group once, by its profile and root, with its projects by the names given here. " +
                "If you leave any out, say how many and why.");
            // Workers listed: what each needs is said too, since no announcement said it (#469)
            List<(SpokenName, AttentionItem)> needs = scope.Shown ? [] : [.. groups.SelectMany(g => g.Projects).Where(p => projects.IsRun(p.Ref))
                .Select(p => (Name: Names.Of(p.Ref), Item: board.ItemOf(p.Ref)?.Item))
                .Where(n => n.Name is not null && n.Item is not null).Select(n => (n.Name!, n.Item!))];
            return SaysItself(text.ToString(), _phrases.Projects([.. groups.Select(g =>
                new ListedGroup(g.Profile, g.Root, [.. g.Projects.Select(ListLabelOf)], g.Server))], left, needs));
        }

        // Too many to keep (#457): a summary by state, then the projects it did not name, a page at a time, on "mere"
        var (states, rest) = ProjectListing.Summarise(ProjectListing.Order(groups, StateOf, ListLabelOf, Activity));
        var pages = ProjectListing.Pages(rest);
        conversation.Reading = pages.Count > 0
            ? new ListReading([.. pages.Select((page, i) => (ListPageResult(page, i, pages.Count, rest.Count), _phrases.ListPage(page, i + 1 < pages.Count)))], 0)
            : null;
        var said = _phrases.ListSummary(kept.Count,
            [.. states.Select(s => (s.State, s.Count, (IReadOnlyList<SpokenName>)[.. s.Named.Select(p => Names.Full(p.Project.Ref) ?? new SpokenName(p.Label))]))],
            pages.Count > 0, left);
        text.Append($"The system said a summary by state itself: \"{said}\"");
        if (pages.Count > 0)
            text.Append($" The {rest.Count} it did not name are read {ProjectListing.Page} at a time, the most recent first: when the user says \"mere\", call {ReadMore}.");
        return SaysItself(text.ToString(), said);
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Which projects a list takes in (#469): those no live overseer runs (<see cref="ProjectBoard.Shown"/>), every one
    /// (<paramref name="All"/>), or the workers of <paramref name="Overseer"/> alone.
    /// </summary>
    private sealed record ListScope(ProjectBoard Board, bool All, ProjectRef? Overseer)
    {
        /// <summary>Unasked: overseers stand for their workers.</summary>
        public bool Shown => !All && Overseer is null;

        public IReadOnlyList<ServerProject> Projects =>
            All ? Board.Projects : Overseer is { } of ? Board.WorkersOf(of) : Board.Shown;

        /// <summary>Whether the scope names the project itself, not on its overseer's line.</summary>
        public bool Takes(ProjectRef project) =>
            All || (Overseer is { } of ? Board.WorkersOf(of).Any(w => w.Ref == project) : !Board.IsRun(project));
    }

    /// <summary>
    /// The scope <paramref name="workers"/> names (<see cref="Workers"/>): every worker for "all", and for a window of all
    /// (<paramref name="since"/> "all", "alle"), which the user said of everything; an overseer's own workers when it names
    /// one, or the one talked about ("current"), or the one overseer there is; none unasked. Null for a name that is no overseer.
    /// </summary>
    private ListScope? ScopeOf(string? workers, string? since)
    {
        var asked = workers?.Trim().ToLowerInvariant();
        switch (asked)
        {
            case null or "":
                return new ListScope(projects, since?.Trim().ToLowerInvariant() is SinceAll or "alle", null);
            case WorkersAll or "alle":
                return new ListScope(projects, true, null);
        }
        var overseer = asked == WorkersCurrent ? conversation.Current : handles.Resolve(workers!);
        if (overseer is not null && projects.WorkersOf(overseer).Count > 0)
            return new ListScope(projects, false, overseer);
        // "Overseerens workers" with no overseer talked about: the one overseer there is
        return asked == WorkersCurrent && Overseers() is [var only] ? new ListScope(projects, false, only.Ref) : null;
    }

    /// <summary>The overseers voice says, those that run workers now.</summary>
    private List<ServerProject> Overseers() => [.. projects.Shown.Where(p => projects.WorkersOf(p.Ref).Count > 0)];

    /// <summary>A <see cref="WorkersParameter"/> that names no overseer: for the model, with the overseers there are.</summary>
    private string NoSuchOverseer(string workers)
    {
        var overseers = Overseers().Select(p => Names.Full(p.Ref)).OfType<SpokenName>().ToList();
        return $"'{workers}' is no overseer that runs workers now. Nothing was listed. " + (overseers.Count > 0
            ? $"Overseers: {string.Join("; ", overseers)}. Ask which, or give {WorkersParameter} \"{WorkersAll}\"."
            : "No overseer runs any worker now.");
    }

    /// <summary>"6 workers, 2 of them waiting on it, which it handles", as a tool's text tells the model.</summary>
    private static string OverseerLine(int workers, int waiting) =>
        (workers == 1 ? "1 worker" : $"{workers} workers") + (waiting > 0 ? $", {waiting} of them waiting on it, which it handles" : "");

    /// <summary>
    /// What a list's line adds for the model (#469): an overseer's workers, counted, when they are not listed; what a
    /// worker needs, when they are.
    /// </summary>
    private string ListedLine(ServerProject project, ListScope scope) =>
        scope.Shown
            ? projects.WorkersOf(project.Ref) is { Count: > 0 } run ? $"; runs {OverseerLine(run.Count, run.Count(w => StateOf(w) == ListedState.NeedsYou))}" : ""
            : board.ItemOf(project.Ref)?.Item is { } item ? $"; needs the user: {Describe(item)}" : "";

    /// <summary>When the project last did something (<see cref="ProjectListing.ActivityOf"/>).</summary>
    private static DateTime ActivityOf(ServerProject project, DateTime now) => ProjectListing.ActivityOf(project.Project, now);

    /// <summary>What the project is doing, as a list says it: it needs the user when it has an attention item.</summary>
    private ListedState StateOf(ServerProject project) => ProjectListing.StateOf(project.Project.State, board.ItemOf(project.Ref) is not null);

    /// <summary>
    /// A list's window as the model gave it (<see cref="Since"/>): <see cref="ListWindow"/>, how the tool's text says it,
    /// and a note for the model, when it is not what was asked for.
    /// </summary>
    private sealed record ListAsk(ListWindow Window, string Said, string Note = "");

    /// <summary>The window <paramref name="since"/> names; null for one it does not.</summary>
    /// <param name="root">The root or profile the list is of: "siden sidst" counts from its own last list too (<see cref="VoiceConversation.LastListedIn"/>).</param>
    private ListAsk? AskOf(string? since, DateTime now, string? root)
    {
        var recent = new ListAsk(ListWindow.Recent(now, _staleAfter), $"in the last {StaleHoursSaid} hours");
        return since?.Trim().ToLowerInvariant() switch
        {
            null or "" => recent,
            SinceAll or "alle" => new ListAsk(ListWindow.All, "ever"),
            SinceLastAsked or "sidst" => conversation.LastListedIn(string.IsNullOrWhiteSpace(root) ? null : root) is { } last
                ? new ListAsk(ListWindow.After(last), $"since the user last asked, at {last:HH:mm} UTC")
                : recent with { Note = " Nothing was listed before in this voice session, so this is the default list: say so." },
            var minutes when int.TryParse(minutes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && m > 0 =>
                new ListAsk(ListWindow.After(now.AddMinutes(-m)), $"in the last {m} minutes"),
            _ => null,
        };
    }

    /// <summary>A window the tool does not know: for the model, with those it does.</summary>
    private static string NoSuchWindow(string since) =>
        $"'{since}' is no window. Nothing was listed. Give {SinceParameter} empty for the default, \"{SinceAll}\" for all, " +
        $"\"{SinceLastAsked}\" for since the user last asked, or a number of minutes.";

    /// <summary>What a list left out (#468), for the model: how many, and why.</summary>
    private static string LeftOutText(LeftOut left, ListAsk ask) => left switch
    {
        { Count: 0 } => "",
        { Asked: true, Waiting: 0 } => $"{left.Count} more had no activity {ask.Said}: left out, and not named.",
        { Asked: true } => $"{left.Count} more had no activity {ask.Said}: left out, and not named; {left.Waiting} of them need the user from before, which the system says.",
        _ => $"{left.Count} stale ones, with no activity {ask.Said}, were left out and not named. If the user asks for all of " +
            $"them, or the old ones too, call {ListProjects} with {SinceParameter} \"{SinceAll}\".",
    };

    /// <summary>The label the project is said by in a list.</summary>
    private string LabelOf(ServerProject project) => handles.LabelOf(project.Ref) ?? project.Project.Name;

    /// <summary>A page of a long list (#457), as the tool's text tells the model what the system said of it.</summary>
    private static string ListPageResult(IReadOnlyList<ListedProject> page, int index, int pages, int left) =>
        $"The list's projects not named in its summary ({left}), part {index + 1} of {pages}, said by the system itself: " +
        string.Join("; ", page.Select(p => $"{p.Label} ({p.State}, profile {p.Profile}, root {p.Root ?? "none"}{(p.Server is { } server ? $", server {server}" : "")})")) +
        (index + 1 < pages ? $". More follows: {ReadMore} reads it." : ". That was the end of the list.");

    /// <summary>
    /// Whether the project is in the root or profile the user named: its profile, its root's name, or its root as it is
    /// shown (its title), case ignored.
    /// </summary>
    private bool In(ServerProject project, string root) =>
        new[] { project.Project.ProfileName ?? "Default", project.Project.RootName, projects.RootShown(project) }
            .Any(n => string.Equals(n, root.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>A root or profile no project is in: for the model, with the groups there are, which it offers as options.</summary>
    private string NoSuchRoot(string root)
    {
        // Nothing listed: "mere" reads on in no earlier list (#507)
        conversation.Reading = null;
        return $"No root or profile '{root}' has any project. Nothing was listed. There are: {string.Join("; ", Names.Groups().Select(g => g.Heading))}.";
    }

    /// <summary>
    /// A list was said at <paramref name="now"/>: what "siden sidst" counts from (#468). One of every project moves it for
    /// all; one of a root or profile for that one alone, and one of an overseer's workers for none (#507), since neither
    /// said anything of the rest.
    /// </summary>
    private void Listed(string? root, ListScope scope, DateTime now)
    {
        if (!string.IsNullOrWhiteSpace(root))
            conversation.ListedIn(root, now);
        else if (scope.Overseer is null)
            conversation.LastListed = now;
    }

    /// <summary>
    /// <paramref name="result"/>, the tool's text for the model, which the code says itself as <paramref name="said"/>: the
    /// model's round after it is not run (#456).
    /// </summary>
    private string SaysItself(string result, string said)
    {
        conversation.SaysItself(result, said);
        return result;
    }

    private static string Count(int projects) => projects == 1 ? "1 project" : $"{projects} projects";

    public async Task<string> ProjectStatusAsync(string? reference, CancellationToken ct) =>
        Target(reference) is { } target && handles.LabelOf(target) is not null ? await StatusOfAsync(target, ct) : await UnknownAsync(reference, ct);

    /// <summary>The project's state and what it waits on, as <see cref="ProjectStatus"/> reads them; it has a handle.</summary>
    private async Task<string> StatusOfAsync(ProjectRef target, CancellationToken ct)
    {
        var name = Names.Of(target)!;
        var status = await servers.GetStatusAsync(target, ct);
        Talked(target);
        // The last line is its status: "Mere?" expands it into its last reply (#455)
        conversation.Reading = new ProjectLine(target, Read: true);
        var text = new StringBuilder($"{name} ({Details(status.Name, status.Kind)}): {status.State}.");
        var item = board.ItemOf(target)?.Item;
        var full = item is null ? null : InFull(item, status);
        var standing = StandingOf(status, item);
        text.Append(Standing(standing));
        if (item is not null)
        {
            text.Append($" Needs the user: {Describe(item, full)}");
            if (item.Spoken is { Length: > 0 })
                text.Append(' ').Append(SpokenBySystem(name, item));
        }
        else if (status.CurrentQuestion is { Length: > 0 } question)
            text.Append($" Asked: {Capped(question)}");
        var failed = status.LastError is { Length: > 0 } && status.State == ProjectState.Error && item?.Kind != AttentionKind.Error;
        if (failed)
            text.Append($" Error: {Capped(status.LastError!)}");
        // An overseer's own line counts the workers it stands for (#469)
        var run = projects.WorkersOf(target);
        var waiting = run.Count(w => StateOf(w) == ListedState.NeedsYou);
        if (run.Count > 0)
            text.Append($" Runs {OverseerLine(run.Count, waiting)}: {WhatNeedsMe} or {ListProjects} with {WorkersParameter} \"{WorkersCurrent}\" lists them.");
        var recap = await AskForRecapAsync(target, status, ct);
        text.Append(recap switch
        {
            RecapAsk.Sent => " It keeps no recap of where it stands: you have asked it for one, which is read the next time the user asks about it.",
            RecapAsk.Asked => " It has been asked for a recap of where it stands, which has not come yet.",
            _ => "",
        });
        var result = ReadOut(text.ToString());
        if (failed || StatusSaid(name, status, item, full, standing) is not { } said) return result;
        if (run.Count > 0) said = $"{said} {GodModeAnnouncementFormatter.Sentence(_phrases.Workers(new SpokenName(name.Label), run.Count, waiting))}";
        if (recap is RecapAsk.Sent or RecapAsk.Asked) said = $"{said} {_phrases.RecapAsked(before: recap == RecapAsk.Asked)}";
        return SaysItself(result, said);
    }

    /// <summary>
    /// Asks a project the user asks about for its recap (#513), when it keeps none and its claude is idle, with nothing
    /// pending and no question: never one that runs or waits on the user. The server sends <c>/recap</c> once for as long
    /// as it tracks the project, whoever asks, so a restart of voice asks no more often; its answer is the project's
    /// <see cref="ProjectStatus.Recap"/>, read the next time it is asked about. Null when it was not asked, or the server
    /// could not be (one that does not know the call, or fails it): the status is read all the same.
    /// </summary>
    private async Task<RecapAsk?> AskForRecapAsync(ProjectRef target, ProjectStatus status, CancellationToken ct)
    {
        if (status is not { Recap: null or "", State: ProjectState.Idle, CurrentQuestion: null or "", PendingPermission: null, PendingQuestion: null })
            return null;
        try { return await servers.AskForRecapAsync(target, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>How much of a last result <see cref="Standing"/> reads when the session gave no line of its own: its start, a few sentences.</summary>
    public const int StandingResultLength = 300;

    /// <summary>What <see cref="StandingOf"/> found: the session's recap, its last spoken reply, or its last result shortened.</summary>
    private enum StandingKind { Recap, Spoken, Result }

    /// <summary>
    /// Where the project stands (issue #466), in this order: the recap the session keeps of it
    /// (<see cref="ProjectStatus.Recap"/>), else, with no attention item to read the turn from, its last spoken reply, else
    /// its last result shortened. Null when it has none of them.
    /// </summary>
    private static (StandingKind Kind, string Text)? StandingOf(ProjectStatus status, AttentionItem? item) => status switch
    {
        { Recap: { Length: > 0 } recap } => (StandingKind.Recap, recap),
        _ when item is not null => null,
        { SpokenSummary: { Length: > 0 } spoken } => (StandingKind.Spoken, spoken),
        { LastResult: { Length: > 0 } result } => (StandingKind.Result, Shortened(result.Trim())),
        _ => null,
    };

    /// <summary>Where the project stands (<see cref="StandingOf"/>), as the tool's text tells the model; nothing when it has none.</summary>
    private static string Standing((StandingKind Kind, string Text)? standing) => standing switch
    {
        (StandingKind.Recap, var recap) => $" Where it stands, in its own words: \"{recap}\"",
        (StandingKind.Spoken, var spoken) => $" Its last reply, in its own spoken words: \"{spoken}\"",
        (StandingKind.Result, var result) => $" Its last result: {result}",
        _ => "",
    };

    /// <summary>The text, or its start to a word at about <see cref="StandingResultLength"/> characters with "…".</summary>
    private static string Shortened(string text) =>
        text.Length <= StandingResultLength ? text
            : text[..(text.LastIndexOf(' ', StandingResultLength) is var at and > 0 ? at : Whole(text, StandingResultLength))] + "…";

    /// <summary>
    /// What the code says itself of a project's status (#456), when all it has to read is said as it is, in the order the
    /// tool's text has it (#466): where it stands (its recap; with no attention item, its last spoken reply, else its last
    /// result shortened), then what it needs (its own spoken reply, or a question or result short and plain enough,
    /// <see cref="SaidAsIs"/>). Null when any of it is not, which the model says: a status with nothing to read, an error, a
    /// permission request, a question asked with no attention item, a long or marked-up text.
    /// </summary>
    private string? StatusSaid(SpokenName name, ProjectStatus status, AttentionItem? item, string? full, (StandingKind Kind, string Text)? standing)
    {
        var stands = standing switch
        {
            null => "",
            (StandingKind.Recap, var recap) => _phrases.Stands(name, recap),
            (StandingKind.Spoken, var spoken) => _phrases.SaidLast(name, spoken),
            (StandingKind.Result, var result) when SaidAsIs(result.TrimEnd('…')) => _phrases.LastResult(name, result),
            _ => null,
        };
        // Named once in a line (#455): after where it stands, by its label alone
        var again = stands is { Length: > 0 } ? new SpokenName(name.Label) : name;
        var needs = item switch
        {
            null when status.CurrentQuestion is { Length: > 0 } => null,
            null => "",
            { Spoken.Length: > 0 } => _phrases.Spoken(again, item),
            { Kind: AttentionKind.Question or AttentionKind.Finished } when SaidAsIs(full) => _phrases.Reads(again, item, full!.Trim()),
            _ => null,
        };
        return stands is null || needs is null ? null
            : string.Join(" ", new[] { stands, needs }.Where(t => t.Length > 0).Select(GodModeAnnouncementFormatter.Sentence)) is { Length: > 0 } said ? said : null;
    }

    /// <summary>How long a question or result the code says as it is may be: a few sentences. A longer one is the model's to shorten.</summary>
    public const int SaidAsIsLength = 300;

    /// <summary>
    /// Whether a project's text can be said as it is: at most <see cref="SaidAsIsLength"/>, one paragraph, and nothing
    /// speech would read out as symbols (markdown, code, paths), which the model summarizes.
    /// </summary>
    internal static bool SaidAsIs(string? text) =>
        text?.Trim() is { Length: > 0 and <= SaidAsIsLength } trimmed && !trimmed.Contains("\n\n", StringComparison.Ordinal)
        && trimmed.IndexOfAny(['`', '*', '#', '|', '[', ']', '{', '}', '<', '>', '\\', '/', '_', '~']) < 0;

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
    public async Task<string> ReadReplyAsync(string? reference, string? turns, CancellationToken ct) =>
        Target(reference) is { } target && handles.LabelOf(target) is not null ? await ReplyOfAsync(target, turns, ct) : await UnknownAsync(reference, ct);

    /// <summary>The project's last replies, as <see cref="ReadReply"/> reads them; it has a handle.</summary>
    private async Task<string> ReplyOfAsync(ProjectRef target, string? turns, CancellationToken ct)
    {
        var name = Names.Of(target)!;
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
    /// What "mere" reads on in, whatever was said last (<see cref="VoiceConversation.Reading"/>). The next page of a long
    /// project list (#457), in the code's words. A line about one project expanded a step (#455, <see cref="ProjectLine"/>):
    /// an announcement into its status, a status into its last reply. Else the next part of the reply
    /// <see cref="ReadReplyAsync"/> read last, or that there is none. A project that has written since (a new reply, or
    /// more of one it was working on) has its old one dropped, and says so (#411): "læs videre" never reads on in a reply
    /// that is no longer its last.
    /// </summary>
    public async Task<string> ReadMoreAsync(CancellationToken ct)
    {
        switch (conversation.Reading)
        {
            case ProjectLine { Read: false } line when handles.LabelOf(line.Project) is not null:
                return await StatusOfAsync(line.Project, ct);
            case ProjectLine line when handles.LabelOf(line.Project) is not null:
                return await ReplyOfAsync(line.Project, null, ct);
            case ProjectLine:
                conversation.Reading = null;
                return "Nothing more to read: the project the last line was about is gone. Say so.";
            case ListReading list when list.Next < list.Pages.Count:
                conversation.Reading = list with { Next = list.Next + 1 };
                return SaysItself(list.Pages[list.Next].Result, list.Pages[list.Next].Said);
            case ListReading:
                return $"Nothing more to read: the project list was read to its end. {ListProjects} lists them again.";
        }
        if (conversation.Reading is not ReplyReading reading || reading.Next >= reading.Parts.Count)
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
        if (Target(reference) is not { } target || handles.LabelOf(target) is null)
            return await UnknownAsync(reference, ct);
        // An announcement that just changed the project talked about never decides where an unnamed answer goes (#461)
        if (string.IsNullOrWhiteSpace(reference) && conversation.TakeAnnouncedSwitch() is { } switched
            && handles.LabelOf(switched.From) is not null && handles.LabelOf(switched.To) is not null
            && Names.Of(switched.From) is { } before && Names.Of(switched.To) is { } announced)
            return SaysItself($"Nothing was sent: {announced} was announced just before this answer, which names no project, " +
                $"and the user was talking about {before} before it. The system asks which of the two it is for. When the user " +
                $"says one, call {Answer} again with the same text and that project named.", _phrases.Which(before, announced));

        // Named once it is known what is said of it: the line names it (#455)
        var name = Names.Of(target)!;
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
        var waiting = (await servers.GetAttentionAsync(ct)).Where(i => !projects.Holds(i)).Select(i => Names.Full(i.Project)).OfType<SpokenName>().ToList();
        var which = waiting.Count > 0 ? $" Waiting now: {string.Join("; ", waiting)}." : " Nothing needs the user now.";
        var all = projects.Shown;
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
            AttentionKind.Question when item.Outcome == TurnOutcome.Blocked => $"blocked: {said}",
            AttentionKind.Question => $"question: {said}",
            AttentionKind.Permission => $"permission request ({item.Permission?.Summary ?? said}); answered on screen only",
            AttentionKind.Error => $"failed: {said}",
            AttentionKind.Escalation => $"needs the user's decision: {said}",
            AttentionKind.Review => $"changes requested on its pull request: {said}",
            // Done only when the session said so; with no outcome it is idle, which says nothing of the work (issue #467)
            AttentionKind.Finished when item.Outcome == TurnOutcome.Done => $"finished: {said}",
            AttentionKind.Finished => $"idle (it did not say it is done): {said}",
        };
    }

    private static string? Argument(IDictionary<string, object?> args, string name) =>
        args.TryGetValue(name, out var value) ? value?.ToString() : null;
}
