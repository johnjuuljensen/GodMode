using System.Text.Json;

namespace GodMode.Shared.Models;

/// <summary>
/// Client-facing information about a create action within a project root.
/// No server paths or scripts exposed — only name, description, and input schema.
/// </summary>
public record CreateActionInfo(
    string Name,
    string? Description = null,
    JsonElement? InputSchema = null
);
