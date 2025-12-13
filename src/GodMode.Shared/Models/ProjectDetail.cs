namespace GodMode.Shared.Models;

/// <summary>
/// Detailed information about a newly created project.
/// </summary>
/// <param name="Status">The project status.</param>
/// <param name="SessionId">The Claude session ID.</param>
public record ProjectDetail(
    ProjectStatus Status,
    string SessionId
);
