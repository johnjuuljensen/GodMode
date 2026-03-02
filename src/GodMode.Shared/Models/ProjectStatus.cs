using GodMode.Shared.Enums;

namespace GodMode.Shared.Models;

/// <summary>
/// Detailed status information about a project.
/// </summary>
/// <param name="Id">The project identifier.</param>
/// <param name="Name">The project name.</param>
/// <param name="State">The current state of the project.</param>
/// <param name="CreatedAt">The timestamp when the project was created.</param>
/// <param name="UpdatedAt">The timestamp when the project was last updated.</param>
/// <param name="CurrentQuestion">The current question waiting for input, if any.</param>
/// <param name="Metrics">Project metrics.</param>
/// <param name="Git">Git status information, if available.</param>
/// <param name="Tests">Test status information, if available.</param>
/// <param name="OutputOffset">The byte offset in the output.jsonl file.</param>
/// <param name="RootName">The name of the project root this project belongs to.</param>
/// <param name="RepoUrl">Deprecated. Kept for backward compatibility with existing status.json files on disk.</param>
public record ProjectStatus(
    string Id,
    string Name,
    ProjectState State,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? CurrentQuestion,
    ProjectMetrics Metrics,
    GitStatus? Git,
    TestStatus? Tests,
    long OutputOffset,
    string? RootName = null,
    string? ProfileName = null,
    string? RepoUrl = null
);
