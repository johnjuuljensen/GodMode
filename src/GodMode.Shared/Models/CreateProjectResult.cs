namespace GodMode.Shared.Models;

/// <summary>
/// What a create made. An action that starts a session gives its project; one that starts none
/// (<see cref="CreateAction.Session"/> off) gives none, and may say what its script made instead.
/// </summary>
/// <param name="Project">The session created, or null when the action starts none.</param>
/// <param name="Message">
/// For an action that starts no session: its create script's <c>message</c> result (the result
/// file's <c>message=</c>), for the app to show; null when it wrote none. Always null with a project.
/// </param>
public record CreateProjectResult(
    ProjectStatus? Project,
    string? Message = null
);
