using GodMode.Shared.Enums;

namespace GodMode.Shared.Models;

/// <summary>
/// Request to create a new project.
/// </summary>
/// <param name="Name">The project name. For worktree projects, this is also the branch name.</param>
/// <param name="ProjectRootName">The name of the project root where the project will be created.</param>
/// <param name="ProjectType">The type of project to create.</param>
/// <param name="RepoUrl">The repository URL to clone (required for GitHubRepo and GitHubWorktree types).</param>
/// <param name="InitialPrompt">The initial prompt to send to Claude.</param>
public record CreateProjectRequest(
    string Name,
    string ProjectRootName,
    ProjectType ProjectType,
    string? RepoUrl,
    string InitialPrompt
);
