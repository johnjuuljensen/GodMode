using System.Text;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Tools;

namespace GodMode.Voice;

/// <summary>
/// The graph's tools on the servers: what needs the user, a project's status, answer it, mark it seen. Their results
/// are for the model, which says them in the user's language. A permission request is never answered here: that is
/// the screen's (issue #285).
/// </summary>
public sealed class VoiceTools(IGodModeServers servers, AttentionBoard board, ProjectHandles handles, VoiceConversation conversation)
{
    public const string WhatNeedsMe = "what_needs_me";
    public const string ProjectStatus = "project_status";
    public const string Answer = "answer_project";
    public const string MarkSeen = "mark_seen";

    public const string ProjectParameter = "project";
    public const string TextParameter = "text";

    private static readonly ToolParameter ProjectReference = new(ProjectParameter,
        "The project's handle as the user said it (a number such as 283, or a word). Leave it empty for the project " +
        "last announced or talked about.", Required: false);

    public ToolSet AddTo(ToolSet tools) => tools
        .Add(WhatNeedsMe,
            "List what needs the user across all their servers: questions, permission requests, errors, reviews and " +
            "finished results, one line per project, oldest first. Call when the user asks what needs them, what is waiting, or for status overall.",
            (_, _, ct) => WhatNeedsMeAsync(ct))
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
        if (items.Count == 0)
            return "Nothing needs the user.";

        var text = new StringBuilder($"{items.Count} need the user:\n");
        foreach (var item in items)
            text.AppendLine($"- {handles.For(item.Project, item.Item.ProjectName)}: {Describe(item.Item)}");
        return text.ToString().TrimEnd();
    }

    public async Task<string> ProjectStatusAsync(string? reference, CancellationToken ct)
    {
        if (await TargetAsync(reference, ct) is not { } target)
            return await UnknownAsync(reference, ct);

        var status = await servers.GetStatusAsync(target, ct);
        conversation.Current = target;
        var handle = handles.For(target, status.Name);
        var text = new StringBuilder($"{handle} ({status.Name}): {status.State}.");
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
        if (await TargetAsync(reference, ct) is not { } target)
            return await UnknownAsync(reference, ct);

        var status = await servers.GetStatusAsync(target, ct);
        var handle = handles.For(target, status.Name);
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
        if (await TargetAsync(reference, ct) is not { } target)
            return await UnknownAsync(reference, ct);

        await servers.MarkSeenAsync(target, ct);
        conversation.Current = target;
        return $"{handles.Of(target) ?? target.ProjectId} is marked seen.";
    }

    /// <summary>
    /// The project named, or the one the conversation is about when none is named. A name no handle matches yet may
    /// be a project that has not needed the user: every server's projects get handles, and it is looked up again.
    /// </summary>
    private async Task<ProjectRef?> TargetAsync(string? reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return conversation.Current;
        if (handles.Resolve(reference) is { } known)
            return known;

        foreach (var project in await servers.ListProjectsAsync(ct))
            handles.For(project.Ref, project.Project.Name);
        return handles.Resolve(reference);
    }

    private async Task<string> UnknownAsync(string? reference, CancellationToken ct)
    {
        var waiting = (await servers.GetAttentionAsync(ct)).Select(i => handles.For(i.Project, i.Item.ProjectName)).ToList();
        var which = waiting.Count > 0 ? $" Waiting now: {string.Join(", ", waiting)}." : " Nothing needs the user now.";
        return string.IsNullOrWhiteSpace(reference)
            ? $"No project is being talked about: ask the user which one.{which}"
            : $"Unknown project '{reference}'.{which}";
    }

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
