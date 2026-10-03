namespace GodMode.Shared.Models;

/// <summary>
/// Client-facing information about a project root directory.
/// No server paths exposed — only name, description, and available create actions.
/// <see cref="Name"/> is the root's key (in IDs, CreateProject, fleet tools); <see cref="Title"/> is what
/// a client shows for it, null when the root has none and its name is shown.
/// </summary>
public record ProjectRootInfo(
    string Name,
    string? Description = null,
    CreateActionInfo[]? Actions = null,
    string? ProfileName = null,
    string? Title = null
);
