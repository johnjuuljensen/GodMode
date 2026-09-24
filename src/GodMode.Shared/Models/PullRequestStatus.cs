using System.Text.Json.Serialization;
using GodMode.Shared.Enums;

namespace GodMode.Shared.Models;

/// <summary>
/// The pull request a project's work became, as its root's <c>status</c> script last reported it
/// (<see cref="ProjectStatus.PullRequest"/>). The server knows nothing of the VCS: the script does.
/// </summary>
/// <param name="Url">The pull request's web page.</param>
/// <param name="Number">Its number.</param>
/// <param name="State">Draft, open, merged or closed.</param>
/// <param name="Review">What the reviews decided.</param>
/// <param name="ChangedAt">When the server first saw this <paramref name="State"/> and <paramref name="Review"/> together; the same after a restart.</param>
public record PullRequestStatus(
    string Url,
    int Number,
    PullRequestState State,
    PullRequestReview Review,
    DateTime ChangedAt)
{
    /// <summary>Whether it can still change: draft or open. A project's status script is polled while it is.</summary>
    [JsonIgnore]
    public bool IsOpen => State is PullRequestState.Draft or PullRequestState.Open;
}
