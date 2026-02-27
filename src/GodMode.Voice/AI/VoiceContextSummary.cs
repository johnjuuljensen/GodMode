namespace GodMode.Voice.AI;

/// <summary>
/// Snapshot of voice session state for system prompt injection.
/// Defined in GodMode.Voice so SystemPromptBuilder can use it
/// without referencing the Avalonia app layer.
/// </summary>
public sealed record VoiceContextSummary(
    string? ActiveProfile,
    string? ActiveServer,
    string? FocusedProject,
    int ProjectCount);
