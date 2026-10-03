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

    /// <summary>
    /// The project's attention item, or null when it needs nothing; with <paramref name="recordedParentId"/>, its parent
    /// as the server recorded it (<see cref="AttentionItem.RecordedParentId"/>).
    /// </summary>
    public static AttentionItem? Of(ProjectStatus status, string? recordedParentId = null)
    {
        var seenAt = status.SeenAt ?? DateTime.MinValue;
        // The turn's spoken reply goes with what its end left: a question in plain text, or its result
        (AttentionKind Kind, DateTime Since, string Text, string? Spoken)? found = status switch
        {
            { PendingPermission: { } permission } => (AttentionKind.Permission, permission.RequestedAt, permission.Summary, null),
            // claude is blocked on an AskUserQuestion: only an answer clears it, whatever the user has seen
            { PendingQuestion: { } question } => (AttentionKind.Question, question.RequestedAt,
                string.Join("\n", question.Questions.Select(q => q.Question)), null),
            // Stopped keeps the question claude was waiting on (a shutdown, or a stop by the user): it still asks.
            // Seen, it needs the user no more, and stays the question a reply answers, until a turn asks again
            { CurrentQuestion: { } question, State: ProjectState.WaitingInput or ProjectState.Stopped }
                when (status.QuestionAt ?? status.UpdatedAt) > seenAt =>
                (AttentionKind.Question, status.QuestionAt ?? status.UpdatedAt, question, status.SpokenSummary),
            // Seen, the project stays Error, and needs the user again when it fails again
            { State: ProjectState.Error } when status.UpdatedAt > seenAt =>
                (AttentionKind.Error, status.UpdatedAt, status.LastError ?? "The project failed.", null),
            // An overseer's question for the user, whatever its turns did since
            { Escalation: { } escalation } when escalation.At > seenAt =>
                (AttentionKind.Escalation, escalation.At, escalation.Text, null),
            { State: ProjectState.Idle or ProjectState.Stopped, PullRequest: { IsOpen: true, Review: PullRequestReview.ChangesRequested } pr }
                when pr.ChangedAt > seenAt =>
                (AttentionKind.Review, pr.ChangedAt, $"Changes requested on pull request #{pr.Number}.", null),
            { State: ProjectState.Idle or ProjectState.Stopped, QuietResult: false, LastResultAt: { } at } when at > seenAt =>
                (AttentionKind.Finished, at, ResultText(status.LastResult), status.SpokenSummary),
            // Quiet turns ended after one that raised Finished: that one's stays until it is seen
            { State: ProjectState.Idle or ProjectState.Stopped, QuietResult: true, UnseenResult: { } unseen } when unseen.At > seenAt =>
                (AttentionKind.Finished, unseen.At, ResultText(unseen.Result), unseen.Spoken),
            _ => null,
        };
        if (found is not ({ } kind, var since, { } text, var spoken)) return null;

        return new AttentionItem(status.Id, status.Name, status.ProfileName, status.RootName, kind, since, PlainText(text),
            kind == AttentionKind.Permission ? status.PendingPermission : null,
            kind == AttentionKind.Question ? status.PendingQuestion : null,
            kind switch
            {
                AttentionKind.Review or AttentionKind.Finished => status.PullRequest?.Url,
                AttentionKind.Escalation => status.Escalation?.Url,
                _ => null,
            },
            spoken is { Length: > 0 } ? spoken : null,
            kind == AttentionKind.Error && status.CreateFailed,
            recordedParentId);
    }

    private static string ResultText(string? result) => result is { Length: > 0 } ? result : "The turn finished.";

    /// <summary>Every project's item, oldest first (by project ID when two are as old).</summary>
    public static AttentionItem[] Of(IEnumerable<ProjectStatus> statuses) => Sorted(statuses.Select(status => Of(status)));

    /// <summary>The items there are, oldest first (by project ID when two are as old).</summary>
    public static AttentionItem[] Sorted(IEnumerable<AttentionItem?> items) =>
        items.OfType<AttentionItem>()
            .OrderBy(item => item.Since).ThenBy(item => item.ProjectId, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Whether the item is the user's (issue #401): all but a child's <see cref="AttentionKind.Finished"/> and
    /// <see cref="AttentionKind.Review"/>, which are its parent's business: the overseer that started it hears of them, and
    /// reports on it. A child's permission prompt, question, error and escalation are the user's.
    /// </summary>
    public static bool IsTheUsers(AttentionItem item) =>
        item is not { RecordedParentId: not null, Kind: AttentionKind.Finished or AttentionKind.Review };

    /// <summary>
    /// Whether two lists say the same: the same projects needing the same, since the same time,
    /// with the same text, spoken text, request and pull request. Compared by those, not by record equality, which would
    /// compare a pending request's input and questions by reference.
    /// </summary>
    public static bool Same(IReadOnlyList<AttentionItem> a, IReadOnlyList<AttentionItem> b) =>
        a.Select(Key).SequenceEqual(b.Select(Key));

    private static (string, AttentionKind, DateTime, string, string?, string?, string?) Key(AttentionItem item) =>
        (item.ProjectId, item.Kind, item.Since, item.Text, item.Permission?.RequestId ?? item.Question?.RequestId, item.PullRequestUrl, item.Spoken);

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
        var length = cut > MaxTextLength / 2 ? cut : TextCut.SafeLength(plain, MaxTextLength - 1);
        return new StringBuilder(plain, 0, length, MaxTextLength).Append('…').ToString();
    }

    /// <summary>A fenced code block, or an unclosed fence to the end of the text.</summary>
    [GeneratedRegex(@"```.*?(```|$)", RegexOptions.Singleline)]
    private static partial Regex CodeFence();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
