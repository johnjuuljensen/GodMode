using System.Text.Json.Serialization;

namespace GodMode.Shared.Enums;

/// <summary>
/// How a session says its turn ended (issue #467), with its <c>speak</c> call's <c>outcome</c>: a turn that ended with
/// the session idle is not, for that, done. A turn that gave none has none (null), and its end raises
/// <see cref="AttentionKind.Finished"/> as every turn's did before outcomes, which voice says as idle, never as done.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TurnOutcome>))]
public enum TurnOutcome
{
    /// <summary>The work is complete: the turn's end raises <see cref="AttentionKind.Finished"/>, said as done.</summary>
    [JsonStringEnumMemberName("done")]
    Done,

    /// <summary>A decision or input is needed: the session waits on the user, a <see cref="AttentionKind.Question"/>.</summary>
    [JsonStringEnumMemberName("needs-you")]
    NeedsYou,

    /// <summary>
    /// The session carries on by itself (a background task, waiting on CI or a review): its end raises nothing, as a
    /// quiet turn's does (<see cref="Models.ProjectStatus.QuietResult"/>).
    /// </summary>
    [JsonStringEnumMemberName("continuing")]
    Continuing,

    /// <summary>It cannot go on, and says why: the session waits on the user, a <see cref="AttentionKind.Question"/>.</summary>
    [JsonStringEnumMemberName("blocked")]
    Blocked,
}
