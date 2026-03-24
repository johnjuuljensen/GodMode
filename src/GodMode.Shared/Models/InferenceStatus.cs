namespace GodMode.Shared.Models;

/// <summary>
/// Reports whether LLM inference is configured and ready.
/// </summary>
public record InferenceStatus(
    bool IsConfigured,
    string? Provider = null,
    string? Model = null,
    string? Error = null
);
