using System.Text.Json;

namespace GodMode.Shared.Models;

/// <summary>
/// Client-facing information about a project root directory.
/// No server paths exposed — only name, description, and input schema.
/// </summary>
public record ProjectRootInfo(
    string Name,
    string? Description = null,
    JsonElement? InputSchema = null
);
