using GodMode.Shared.Enums;

namespace GodMode.Shared.Models;

/// <summary>
/// Summary information about a project.
/// </summary>
/// <param name="Id">The project identifier.</param>
/// <param name="Name">The project name.</param>
/// <param name="State">The current state of the project.</param>
/// <param name="UpdatedAt">The timestamp when the project was last updated.</param>
/// <param name="CurrentQuestion">The current question waiting for input, if any.</param>
public record ProjectSummary(
    string Id,
    string Name,
    ProjectState State,
    DateTime UpdatedAt,
    string? CurrentQuestion = null
);
