using System.Text.Json.Serialization;

namespace GodMode.Shared.Enums;

/// <summary>Where a project's pull request is (<see cref="Models.PullRequestStatus.State"/>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PullRequestState>))]
public enum PullRequestState
{
    Draft,
    Open,
    Merged,
    Closed,
}
