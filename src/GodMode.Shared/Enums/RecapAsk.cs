using System.Text.Json.Serialization;

namespace GodMode.Shared.Enums;

/// <summary>What became of asking a session for its recap (the hub's AskForRecap, issue #513).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RecapAsk>))]
public enum RecapAsk
{
    /// <summary><c>/recap</c> was sent: its answer becomes the session's <see cref="Models.ProjectStatus.Recap"/> when it comes.</summary>
    [JsonStringEnumMemberName("sent")]
    Sent,

    /// <summary>It was asked before, and nothing is sent: the recap is on its way, or it gave none.</summary>
    [JsonStringEnumMemberName("asked")]
    Asked,

    /// <summary>The session has a recap: nothing is sent.</summary>
    [JsonStringEnumMemberName("has-recap")]
    HasRecap,

    /// <summary>The session cannot take it now (it runs, waits on a permission or a question, or its claude is stopped): nothing is sent.</summary>
    [JsonStringEnumMemberName("busy")]
    Busy,
}
