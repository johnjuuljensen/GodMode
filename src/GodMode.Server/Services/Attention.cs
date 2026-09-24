using System.Text;
using System.Text.RegularExpressions;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// What a project needs from the user, derived from its status alone, so the answer after a server
/// restart is the one before it: every field read is persisted in status.json, except the pending
/// requests, which do not outlive their process.
/// </summary>
public static partial class Attention
{
    /// <summary>About how long a text may be; it is cut at a word before this.</summary>
    public const int MaxTextLength = 500;

    /// <summary>The project's attention item, or null when it needs nothing.</summary>
    public static AttentionItem? Of(ProjectStatus status)
    {
        (AttentionKind Kind, DateTime Since, string Text)? found = status switch
        {
            { PendingPermission: { } permission } => (AttentionKind.Permission, permission.RequestedAt, permission.Summary),
            { PendingQuestion: { } question } => (AttentionKind.Question, question.RequestedAt,
                string.Join("\n", question.Questions.Select(q => q.Question))),
            // Stopped keeps the question claude was waiting on (a shutdown, or a stop by the user): it still asks
            { CurrentQuestion: { } question, State: ProjectState.WaitingInput or ProjectState.Stopped } =>
                (AttentionKind.Question, status.QuestionAt ?? status.UpdatedAt, question),
            { State: ProjectState.Error } => (AttentionKind.Error, status.UpdatedAt, status.LastError ?? "The project failed."),
            { State: ProjectState.Idle or ProjectState.Stopped, PullRequest: { IsOpen: true, Review: PullRequestReview.ChangesRequested } pr }
                when pr.ChangedAt > (status.SeenAt ?? DateTime.MinValue) =>
                (AttentionKind.Review, pr.ChangedAt, $"Changes requested on pull request #{pr.Number}."),
            { State: ProjectState.Idle or ProjectState.Stopped, LastResultAt: { } at } when at > (status.SeenAt ?? DateTime.MinValue) =>
                (AttentionKind.Finished, at, status.LastResult is { Length: > 0 } result ? result : "The turn finished."),
            _ => null,
        };
        if (found is not ({ } kind, var since, { } text)) return null;

        return new AttentionItem(status.Id, status.Name, status.ProfileName, status.RootName, kind, since, PlainText(text),
            kind == AttentionKind.Permission ? status.PendingPermission : null,
            kind == AttentionKind.Question ? status.PendingQuestion : null,
            kind is AttentionKind.Review or AttentionKind.Finished ? status.PullRequest?.Url : null);
    }

    /// <summary>Every project's item, oldest first (by project ID when two are as old).</summary>
    public static AttentionItem[] Of(IEnumerable<ProjectStatus> statuses) =>
        statuses.Select(Of).OfType<AttentionItem>()
            .OrderBy(item => item.Since).ThenBy(item => item.ProjectId, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Whether two lists say the same: the same projects needing the same, since the same time,
    /// with the same text, request and pull request. Compared by those, not by record equality, which would
    /// compare a pending request's input and questions by reference.
    /// </summary>
    public static bool Same(IReadOnlyList<AttentionItem> a, IReadOnlyList<AttentionItem> b) =>
        a.Select(Key).SequenceEqual(b.Select(Key));

    private static (string, AttentionKind, DateTime, string, string?, string?) Key(AttentionItem item) =>
        (item.ProjectId, item.Kind, item.Since, item.Text, item.Permission?.RequestId ?? item.Question?.RequestId, item.PullRequestUrl);

    /// <summary>
    /// Text to show on a phone or read aloud: code blocks become "(code)", markdown's backticks go,
    /// whitespace runs collapse to one space, and it is cut at a word to about <see cref="MaxTextLength"/>.
    /// </summary>
    public static string PlainText(string text)
    {
        var plain = CodeFence().Replace(text, " (code) ");
        plain = Whitespace().Replace(plain.Replace("`", ""), " ").Trim();
        if (plain.Length <= MaxTextLength) return plain;

        var cut = plain.LastIndexOf(' ', MaxTextLength - 1);
        return new StringBuilder(plain, 0, cut > MaxTextLength / 2 ? cut : MaxTextLength - 1, MaxTextLength).Append('…').ToString();
    }

    /// <summary>A fenced code block, or an unclosed fence to the end of the text.</summary>
    [GeneratedRegex(@"```.*?(```|$)", RegexOptions.Singleline)]
    private static partial Regex CodeFence();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
