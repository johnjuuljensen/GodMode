using System.Text.Json;

namespace GodMode.Shared.Models;

/// <summary>
/// Configuration for a project root directory, read from .godmode-root.json.
/// Defines the creation workflow: what inputs are needed, what scripts to run,
/// what env vars to set, and what Claude args to use.
/// </summary>
public record RootConfig(
    string? Description = null,
    JsonElement? InputSchema = null,
    string[]? Setup = null,
    string[]? Bootstrap = null,
    string[]? Teardown = null,
    Dictionary<string, string>? Environment = null,
    string[]? ClaudeArgs = null,
    string? NameTemplate = null,
    string? PromptTemplate = null,
    bool IsDefault = false
);
