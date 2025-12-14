namespace GodMode.Shared.Enums;

/// <summary>
/// The type of project being created.
/// </summary>
public enum ProjectType
{
    /// <summary>
    /// Raw folder project, new or existing. Does not require an account.
    /// </summary>
    RawFolder,

    /// <summary>
    /// Basic GitHub repository clone. Requires git credentials on server.
    /// Creation clones the repo into a path named for the project.
    /// </summary>
    GitHubRepo,

    /// <summary>
    /// GitHub repository with worktree workflow.
    /// The project name is also the branch name.
    /// Upon creation, repo is cloned into .{repo}_bare if not already existing.
    /// Branches are created as worktree folders from .{repo}_bare named after the branch.
    /// </summary>
    GitHubWorktree
}
