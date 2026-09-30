using System.Text;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Tools;

namespace GodMode.Voice;

/// <summary>
/// The graph's tools on the servers: what needs the user, which projects there are, a project's status, answer it,
/// mark it seen. Their results are for the model, which says them in the user's language. A permission request is
/// never answered here: that is the screen's (issue #285).
/// </summary>
public sealed class VoiceTools(IGodModeServers servers, AttentionBoard board, ProjectBoard projects, ProjectHandles handles,
    VoiceConversation conversation)
{
    public const string WhatNeedsMe = "what_needs_me";
    public const string ListProjects = "list_projects";
    public const string ProjectStatus = "project_status";
    public const string Answer = "answer_project";
    public const string MarkSeen = "mark_seen";

    public const string ProjectParameter = "project";
    public const string TextParameter = "text";

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
            "Read one project's state and what it waits on (its question, result, error or permission request) in full. " +
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
            (_, args, ct) => MarkSeenAsync(Argument(args, ProjectParameter), ct));

    public async Task<string> WhatNeedsMeAsync(CancellationToken ct)
    {
        var items = await servers.GetAttentionAsync(ct);
        // What was read out is what the conversation is about now: one project, or none to answer unnamed
        conversation.Current = items is [var only] ? only.Project : null;
        if (items.Count == 0)
            return "Nothing needs the user.";

        var text = new StringBuilder($"{items.Count} need the user:\n");
        foreach (var item in items)
            text.AppendLine($"- {handles.For(item.Project, item.Item.ProjectName)}: {Describe(item.Item)}");
        return text.ToString().TrimEnd();
    }

    public string ListProjectsText()
    {
        var all = projects.Projects;
        // As what needs me: one project read out is the one talked about, several leave none
        conversation.Current = all is [var only] ? only.Ref : null;
        if (all.Count == 0)
            return "No projects on any server.";

        var text = new StringBuilder($"{all.Count} projects:\n");
        foreach (var project in all)
            text.AppendLine($"- {Line(project)}");
        return text.ToString().TrimEnd();
    }

    public async Task<string> ProjectStatusAsync(string? reference, CancellationToken ct)
    {
        if (Target(reference) is not { } target)
            return await UnknownAsync(reference, ct);

        var status = await servers.GetStatusAsync(target, ct);
        conversation.Current = target;
        var handle = handles.For(target, status.Name, status.RootName, status.Kind);
        var text = new StringBuilder($"{handle} ({Where(status.Name, status.RootName, status.ProfileName, status.Kind)}): {status.State}.");
        if (board.ItemOf(target) is { } item)
            text.Append($" Needs the user: {Describe(item.Item)}");
        else if (status.CurrentQuestion is { Length: > 0 } question)
            text.Append($" Asked: {question}");
        if (status.LastError is { Length: > 0 } error && status.State == ProjectState.Error)
            text.Append($" Error: {error}");
        return text.ToString();
    }

    public async Task<string> AnswerAsync(string? reference, string? answer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return "No answer given: ask the user what to answer.";
        if (Target(reference) is not { } target)
            return await UnknownAsync(reference, ct);

        var status = await servers.GetStatusAsync(target, ct);
        var handle = handles.For(target, status.Name, status.RootName, status.Kind);
        if (status.PendingPermission is { } permission)
        {
            conversation.Current = target;
            return $"{handle} is waiting on a permission request ({permission.Summary}), which is answered on screen, " +
                "not by voice. Nothing was sent: tell the user to answer it on screen.";
        }

        await servers.ReplyAsync(target, answer.Trim(), ct);
        conversation.Current = target;
        return $"Sent to {handle}: \"{answer.Trim()}\". It continues.";
    }

    public async Task<string> MarkSeenAsync(string? reference, CancellationToken ct)
    {
        if (Target(reference) is not { } target)
            return await UnknownAsync(reference, ct);

        await servers.MarkSeenAsync(target, ct);
        conversation.Current = target;
        return $"{handles.Of(target) ?? target.ProjectId} is marked seen.";
    }

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
        var waiting = (await servers.GetAttentionAsync(ct)).Select(i => handles.For(i.Project, i.Item.ProjectName, i.Item.Root)).ToList();
        var which = waiting.Count > 0 ? $" Waiting now: {string.Join(", ", waiting)}." : " Nothing needs the user now.";
        var all = projects.Projects;
        var options = all.Count == 0 ? " No projects on any server."
            : $" Projects: {string.Join("; ", all.Take(OptionsListed).Select(Line))}{(all.Count > OptionsListed ? $"; {all.Count - OptionsListed} more" : "")}.";
        return string.IsNullOrWhiteSpace(reference)
            ? $"No project is being talked about: ask the user which one.{which}{options}"
            : $"Unknown project '{reference}'.{which}{options}";
    }

    /// <summary>"testing (testing, Assistant, Outbound, chat): Idle".</summary>
    private string Line(ServerProject project)
    {
        var p = project.Project;
        return $"{handles.For(project.Ref, p.Name, p.RootName, p.Kind)} ({Where(p.Name, p.RootName, p.ProfileName, p.Kind)}): {p.State}";
    }

    /// <summary>The project's name, then its root, profile and kind, those it has.</summary>
    private static string Where(string name, string? root, string? profile, string? kind) =>
        string.Join(", ", new[] { name, root, profile, kind }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase));

    private static string Describe(AttentionItem item) => item.Kind switch
    {
        AttentionKind.Question => $"question: {item.Text}",
        AttentionKind.Permission => $"permission request ({item.Permission?.Summary ?? item.Text}); answered on screen only",
        AttentionKind.Error => $"failed: {item.Text}",
        AttentionKind.Review => $"changes requested on its pull request: {item.Text}",
        AttentionKind.Finished => $"finished: {item.Text}",
    };

    private static string? Argument(IDictionary<string, object?> args, string name) =>
        args.TryGetValue(name, out var value) ? value?.ToString() : null;
}
