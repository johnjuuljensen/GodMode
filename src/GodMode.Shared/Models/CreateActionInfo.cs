using System.Text.Json;

namespace GodMode.Shared.Models;

/// <summary>
/// Client-facing information about a create action within a project root.
/// No server paths or scripts exposed — only name, description, and input schema.
/// </summary>
/// <param name="AllowSkipPermissions">
/// Whether the action's root allows skip-permissions: only then does the create form offer the
/// schema's <c>skipPermissions</c>, and only then does the server accept it.
/// </param>
/// <param name="Session">
/// Whether the action starts a session. One that does not only runs its scripts: the create form
/// offers no model for it, and its create returns no project to open (<see cref="CreateProjectResult"/>).
/// </param>
public record CreateActionInfo(
    string Name,
    string? Description = null,
    JsonElement? InputSchema = null,
    string? Model = null,
    bool AllowSkipPermissions = false,
    bool Session = true
);
