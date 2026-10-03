using System.Text.Json.Serialization;

namespace GodMode.Shared.Enums;

/// <summary>What a project needs from the user, most urgent first: a project is listed once, under the first that applies.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AttentionKind>))]
public enum AttentionKind
{
    /// <summary>A tool call waits to be allowed or denied: <see cref="Models.AttentionItem.Permission"/>.</summary>
    Permission,

    /// <summary>
    /// claude asked something: an AskUserQuestion (<see cref="Models.AttentionItem.Question"/>), which only an
    /// answer clears, or its turn ended on a question in plain text (<see cref="Models.ProjectStatus.CurrentQuestion"/>),
    /// which a project stopped since still asks. A question in plain text is cleared by
    /// <see cref="Hubs.IProjectHub.MarkSeen"/> and by any reply, until a turn asks again; seen, the project is still
    /// waiting on it, and a reply still answers it.
    /// </summary>
    Question,

    /// <summary>
    /// The project failed: <see cref="Models.ProjectStatus.LastError"/>. Cleared by <see cref="Hubs.IProjectHub.MarkSeen"/>,
    /// until it fails again; the project stays in <see cref="ProjectState.Error"/>.
    /// </summary>
    Error,

    /// <summary>
    /// An overseer asked the user to decide something (its fleet tool <c>escalate</c>,
    /// <see cref="Models.ProjectStatus.Escalation"/>): the item's text, and its URL as
    /// <see cref="Models.AttentionItem.PullRequestUrl"/> when it gave one. Unlike <see cref="Finished"/>, the turns that
    /// end after it leave it as it is. Cleared by <see cref="Hubs.IProjectHub.MarkSeen"/> and by the user's own reply, answer
    /// or input, not by the fleet's send or a resume, until it asks again.
    /// </summary>
    Escalation,

    /// <summary>
    /// A reviewer asked for changes on the project's open pull request (<see cref="Models.ProjectStatus.PullRequest"/>),
    /// and the project is Idle or Stopped. Cleared by <see cref="Hubs.IProjectHub.MarkSeen"/> and by any reply,
    /// until the pull request changes again.
    /// </summary>
    Review,

    /// <summary>
    /// The turn ended with a result the user has not seen (<see cref="Models.ProjectStatus.LastResult"/>),
    /// and the project is Idle or Stopped. Cleared by <see cref="Hubs.IProjectHub.MarkSeen"/> and by any reply.
    /// A quiet turn's end raises none (<see cref="Models.ProjectStatus.QuietResult"/>).
    /// </summary>
    Finished,
}
