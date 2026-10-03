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
    /// A reviewer asked for changes on the project's open pull request (<see cref="Models.ProjectStatus.PullRequest"/>),
    /// and the project is Idle or Stopped. Cleared by <see cref="Hubs.IProjectHub.MarkSeen"/> and by any reply,
    /// until the pull request changes again.
    /// </summary>
    Review,

    /// <summary>
    /// The turn ended with a result the user has not seen (<see cref="Models.ProjectStatus.LastResult"/>),
    /// and the project is Idle or Stopped. Cleared by <see cref="Hubs.IProjectHub.MarkSeen"/> and by any reply.
    /// </summary>
    Finished,
}
