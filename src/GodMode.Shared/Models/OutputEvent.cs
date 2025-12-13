using GodMode.Shared.Enums;

namespace GodMode.Shared.Models;

/// <summary>
/// Represents an output event from the Claude agent.
/// </summary>
/// <param name="Timestamp">The timestamp when the event occurred.</param>
/// <param name="Type">The type of the output event.</param>
/// <param name="Content">The content of the event.</param>
/// <param name="Metadata">Optional metadata associated with the event.</param>
public record OutputEvent(
    DateTime Timestamp,
    OutputEventType Type,
    string Content,
    Dictionary<string, object>? Metadata = null
);
