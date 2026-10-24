namespace GodMode.Shared.Models;

/// <summary>
/// Client-facing information about a project root directory.
/// No server paths exposed — only name, description, and available create actions.
/// <see cref="Name"/> is the root's key (in IDs, CreateProject, fleet tools); <see cref="Title"/> is what
/// a client shows for it, null when the root has none and its name is shown.
/// <see cref="Account"/> is the Claude account its sessions run on (issue #600), the key of its
/// <see cref="AccountUsage"/>; <see cref="UsageCeiling"/> is its ceiling, its own over its profile's, null when it has none.
/// </summary>
public record ProjectRootInfo(
    string Name,
    string? Description = null,
    CreateActionInfo[]? Actions = null,
    string? ProfileName = null,
    string? Title = null,
    string? Account = null,
    UsageCeiling? UsageCeiling = null
);
