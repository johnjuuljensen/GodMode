using System.Text.Json;

namespace GodMode.Shared.Models;

/// <summary>
/// A named create action within a project root.
/// Each action defines its own input schema, scripts, templates, and configuration.
/// </summary>
public record CreateAction(
    string Name,
    string? Description = null,
    JsonElement? InputSchema = null,
    string[]? Prepare = null,
    string[]? Create = null,
    string[]? Delete = null,
    Dictionary<string, string>? Environment = null,
    string[]? ClaudeArgs = null,
    string? NameTemplate = null,
    string? PromptTemplate = null,
    bool ScriptsCreateFolder = false,
    string? Model = null
);
