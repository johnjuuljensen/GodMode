using System.Text.Json.Serialization;

namespace GodMode.Shared.Enums;

/// <summary>What the reviews of a project's pull request decided (<see cref="Models.PullRequestStatus.Review"/>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PullRequestReview>))]
public enum PullRequestReview
{
    /// <summary>No review has decided yet.</summary>
    None,

    /// <summary>A reviewer asked for changes: the project needs the user (<see cref="AttentionKind.Review"/>).</summary>
    ChangesRequested,

    Approved,
}
