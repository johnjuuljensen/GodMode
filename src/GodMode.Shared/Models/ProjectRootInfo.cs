namespace GodMode.Shared.Models;

/// <summary>
/// Client-facing information about a project root directory.
/// No server paths exposed — only name, description, and available create actions.
/// </summary>
public record ProjectRootInfo(
    string Name,
    string? Description = null,
    CreateActionInfo[]? Actions = null,
    string? ProfileName = null,
    bool HasConfig = false
);
