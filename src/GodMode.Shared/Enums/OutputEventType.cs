using System.Text.Json.Serialization;

namespace GodMode.Shared.Enums;

/// <summary>
/// Represents the type of output event from the Claude agent.
/// These map directly to the "type" field in Claude's stream-json output.
/// Values use JsonStringEnumMemberName for proper case-insensitive serialization.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OutputEventType>))]
public enum OutputEventType
{
    /// <summary>
    /// System initialization or system message.
    /// </summary>
    [JsonStringEnumMemberName("system")]
    System,

    /// <summary>
    /// User input/prompt.
    /// </summary>
    [JsonStringEnumMemberName("user")]
    User,

    /// <summary>
    /// Assistant's response.
    /// </summary>
    [JsonStringEnumMemberName("assistant")]
    Assistant,

    /// <summary>
    /// Final result of a conversation turn.
    /// </summary>
    [JsonStringEnumMemberName("result")]
    Result,

    /// <summary>
    /// Error message.
    /// </summary>
    [JsonStringEnumMemberName("error")]
    Error,

    /// <summary>
    /// claude started a new conversation in the same process (<c>/clear</c>): a new session ID follows, in a new
    /// <c>system/init</c>, and the session's output starts over.
    /// </summary>
    [JsonStringEnumMemberName("conversation_reset")]
    ConversationReset
}
