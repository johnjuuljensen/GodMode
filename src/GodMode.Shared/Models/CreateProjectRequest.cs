namespace GodMode.Shared.Models;

/// <summary>
/// Request to create a new project.
/// </summary>
/// <param name="Name">The project name.</param>
/// <param name="RepoUrl">The repository URL to clone, if any.</param>
/// <param name="InitialPrompt">The initial prompt to send to Claude.</param>
public record CreateProjectRequest(
    string Name,
    string? RepoUrl,
    string InitialPrompt
);
