using System.Globalization;
using System.Text;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Tools;

namespace GodMode.Voice;

/// <summary>
/// The graph's tools on the servers: what needs the user, which projects there are, a project's status, answer it,
/// mark it seen, and start one (read back only: the user's yes creates it, <see cref="ConfirmCreateNode"/>). Their results are for the model, which says them in the user's language. A permission request is
/// never answered here: that is the screen's (issue #285).
/// </summary>
public sealed class VoiceTools(IGodModeServers servers, AttentionBoard board, ProjectBoard projects, ProjectHandles handles,
    VoiceConversation conversation, TimeProvider? time = null)
{
    public const string WhatNeedsMe = "what_needs_me";
    public const string ListProjects = "list_projects";
    public const string ProjectStatus = "project_status";
    public const string Answer = "answer_project";
    public const string MarkSeen = "mark_seen";
    public const string StartSession = "start_session";

    public const string ProjectParameter = "project";
    public const string TextParameter = "text";
    public const string RootParameter = "root";
    public const string ActionParameter = "action";
    public const string IssueParameter = "issue";
    public const string NameParameter = "name";
    public const string PromptParameter = "prompt";

    /// <summary>What the conversation is about, and whether the final being answered was heard more than one way.</summary>
    public VoiceConversation Conversation => conversation;

    /// <summary>The creates voice reads back, and makes on the user's yes.</summary>
    public SessionCreates Creates { get; } = new(servers, handles, time);

    /// <summary>How many projects a reference to none lists, as the options the model offers.</summary>
    private const int OptionsListed = 8;

    private static readonly ToolParameter ProjectReference = new(ProjectParameter,
        "The project's handle as the user said it (a number such as 283, or a word), or its root or kind when the user " +
        "names it so (\"Assistant\", \"chat\"). Leave it empty for the project last announced or talked about.", Required: false);

    public ToolSet AddTo(ToolSet tools) => tools
        .Add(WhatNeedsMe,
            "List what needs the user across all their servers: questions, permission requests, errors, reviews and " +
            "finished results, one line per project, oldest first. Call when the user asks what needs them, what is waiting, or for status overall.",
            (_, _, ct) => WhatNeedsMeAsync(ct))
        .Add(ListProjects,
            "List every project on every server, whether it needs the user or not: its handle, name, root, profile, kind " +
            "and state, the one changed last first. Call when the user asks which projects there are, what runs, or " +
            "about one they just started.",
            (_, _, _) => Task.FromResult(ListProjectsText()))
        .Add(ProjectStatus,
            "Read one project's state and what it waits on (its question, result, error or permission request) in full: " +
            "a very long one has its middle cut, and says so. " +
            "Call when the user asks about one project, or to hear a question or result.",
            [ProjectReference],
            (_, args, ct) => ProjectStatusAsync(Argument(args, ProjectParameter), ct))
        .Add(Answer,
            "Send the user's answer to a project: it reaches the Claude session as the user's reply, and the session " +
            "continues. Give the answer as the instruction the user meant, in their words.",
            [new ToolParameter(TextParameter, "The answer to send, e.g. \"Brug den eksisterende migration.\""), ProjectReference],
            (_, args, ct) => AnswerAsync(Argument(args, ProjectParameter), Argument(args, TextParameter), ct))
        .Add(MarkSeen,
            "Mark a project's finished result as seen, so it no longer needs the user. Call when the user says they have heard it or it is done with.",
            [ProjectReference],
            (_, args, ct) => MarkSeenAsync(Argument(args, ProjectParameter), ct))
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
        foreach (var item in items)
            text.AppendLine($"- {handles.Of(item.Project)}: {Describe(item.Item)}");
        return text.ToString().TrimEnd();
    }

    public string ListProjectsText()
    {
        var all = projects.Projects;
        // As what needs me: one project read out is the one talked about, several leave none
        Talked(all is [var only] ? only.Ref : null);
        if (all.Count == 0)
            return "No projects on any server.";

        var text = new StringBuilder($"{all.Count} projects:\n");
        foreach (var project in all)
            text.AppendLine($"- {Line(project)}");
        return text.ToString().TrimEnd();
    }

    public async Task<string> ProjectStatusAsync(string? reference, CancellationToken ct)
    {
        if (Target(reference) is not { } target || handles.Of(target) is not { } handle)
            return await UnknownAsync(reference, ct);

        var status = await servers.GetStatusAsync(target, ct);
        Talked(target);
        var text = new StringBuilder($"{handle} ({Where(status.Name, status.RootName, status.ProfileName, status.Kind)}): {status.State}.");
        var item = board.ItemOf(target)?.Item;
        if (item is not null)
            text.Append($" Needs the user: {Describe(item, InFull(item, status))}");
        else if (status.CurrentQuestion is { Length: > 0 } question)
            text.Append($" Asked: {Capped(question)}");
        if (status.LastError is { Length: > 0 } error && status.State == ProjectState.Error && item?.Kind != AttentionKind.Error)
            text.Append($" Error: {Capped(error)}");
        return text.ToString();
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

    public async Task<string> AnswerAsync(string? reference, string? answer, CancellationToken ct)
    {
        if (NotOnAGuess() is { } refused)
            return refused;
        if (string.IsNullOrWhiteSpace(answer))
            return "No answer given: ask the user what to answer.";
        if (Target(reference) is not { } target || handles.Of(target) is not { } handle)
            return await UnknownAsync(reference, ct);

        var status = await servers.GetStatusAsync(target, ct);
        if (status.PendingPermission is { } permission)
        {
            conversation.Current = target;
            return $"{handle} is waiting on a permission request ({permission.Summary}), which is answered on screen, " +
                "not by voice. Nothing was sent: tell the user to answer it on screen.";
        }

        await servers.ReplyAsync(target, answer.Trim(), ct);
        conversation.Current = target;
        conversation.Sent(handle);
        return $"Sent to {handle}: \"{answer.Trim()}\". It continues. The system says it was sent itself.";
    }

    public async Task<string> MarkSeenAsync(string? reference, CancellationToken ct)
    {
        if (NotOnAGuess() is { } refused)
            return refused;
        if (Target(reference) is not { } target)
            return await UnknownAsync(reference, ct);

        await servers.MarkSeenAsync(target, ct);
        conversation.Current = target;
        return $"{handles.Of(target) ?? target.ProjectId} is marked seen.";
    }

    /// <summary>
    /// A tool that acts (<paramref name="tool"/>: muting announcements, say), made to do nothing on a final heard more
    /// than one way, as <see cref="AnswerAsync"/> and <see cref="MarkSeenAsync"/> do.
    /// </summary>
    public VoiceTool Acting(VoiceTool tool) => tool with
    {
        Handler = (context, args, ct) => NotOnAGuess() is { } refused ? Task.FromResult(refused) : tool.Handler(context, args, ct),
    };

    /// <summary>
    /// Why a tool that acts does nothing now: the final being answered was heard more than one way, so what the user
    /// meant is a guess, and nothing is sent or changed on a guess. Null when it was heard one way.
    /// </summary>
    private string? NotOnAGuess() => conversation.Unsure is { } unsure
        ? $"Nothing was done: the user was heard more than one way, \"{unsure.Final}\" and earlier " +
          $"{string.Join(", ", unsure.Readings.Select(r => $"\"{r}\""))}. Ask which they meant, as a closed question " +
          "naming the project (e.g. \"Mente du ja eller nej til 283?\"), and act only on their next answer."
        : null;

    /// <summary>What the conversation is about from now on, from a tool that read it out; left as it is on a final heard more than one way.</summary>
    private void Talked(ProjectRef? project)
    {
        if (conversation.Unsure is null)
            conversation.Current = project;
    }

    /// <summary>What to read back for a create, or ask, or why there is none (<see cref="SessionCreates.Propose"/>).</summary>
    public async Task<string> StartSessionAsync(CreateAsk ask, CancellationToken ct) =>
        Creates.Propose(await servers.ListRootsAsync(ct), ask);

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
        var waiting = (await servers.GetAttentionAsync(ct)).Select(i => handles.Of(i.Project)).OfType<string>().ToList();
        var which = waiting.Count > 0 ? $" Waiting now: {string.Join(", ", waiting)}." : " Nothing needs the user now.";
        var all = projects.Projects;
        var options = all.Count == 0 ? " No projects on any server."
            : $" Projects: {string.Join("; ", all.Take(OptionsListed).Select(Line))}{(all.Count > OptionsListed ? $"; {all.Count - OptionsListed} more" : "")}.";
        return string.IsNullOrWhiteSpace(reference)
            ? $"No project is being talked about: ask the user which one.{which}{options}"
            : handles.IsRetired(reference)
                ? $"Unknown project '{reference}': that project was deleted. Nothing was done.{which}{options}"
                : $"Unknown project '{reference}'.{which}{options}";
    }

    /// <summary>"testing (testing, Assistant, Outbound, chat): Idle".</summary>
    private string Line(ServerProject project)
    {
        var p = project.Project;
        return $"{handles.Of(project.Ref) ?? p.Name} ({Where(p.Name, p.RootName, p.ProfileName, p.Kind)}): {p.State}";
    }

    /// <summary>The project's name, then its root, profile and kind, those it has.</summary>
    private static string Where(string name, string? root, string? profile, string? kind) =>
        string.Join(", ", new[] { name, root, profile, kind }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>The item as a line says it, with <paramref name="text"/> for its text: the item's own (cut) one when null.</summary>
    private static string Describe(AttentionItem item, string? text = null)
    {
        var said = text ?? item.Text;
        return item.Kind switch
        {
            AttentionKind.Question => $"question: {said}",
            AttentionKind.Permission => $"permission request ({item.Permission?.Summary ?? said}); answered on screen only",
            AttentionKind.Error => $"failed: {said}",
            AttentionKind.Review => $"changes requested on its pull request: {said}",
            AttentionKind.Finished => $"finished: {said}",
        };
    }

    private static string? Argument(IDictionary<string, object?> args, string name) =>
        args.TryGetValue(name, out var value) ? value?.ToString() : null;
}
