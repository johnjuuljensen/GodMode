namespace GodMode.Shared.Enums;

/// <summary>
/// Represents the type of output event from the Claude agent.
/// </summary>
public enum OutputEventType
{
    /// <summary>
    /// User input text.
    /// </summary>
    UserInput,

    /// <summary>
    /// Assistant's text response.
    /// </summary>
    AssistantOutput,

    /// <summary>
    /// Assistant's thinking process.
    /// </summary>
    Thinking,

    /// <summary>
    /// Tool usage by the assistant.
    /// </summary>
    ToolUse,

    /// <summary>
    /// Result from tool execution.
    /// </summary>
    ToolResult,

    /// <summary>
    /// Error message.
    /// </summary>
    Error,

    /// <summary>
    /// System message.
    /// </summary>
    System
}
