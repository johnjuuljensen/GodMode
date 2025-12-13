namespace GodMode.Shared.Models;

/// <summary>
/// Git status information for a project.
/// </summary>
/// <param name="Branch">The current branch name.</param>
/// <param name="LastCommit">The last commit hash.</param>
/// <param name="UncommittedChanges">The number of uncommitted changes.</param>
/// <param name="UntrackedFiles">The number of untracked files.</param>
public record GitStatus(
    string? Branch,
    string? LastCommit,
    int UncommittedChanges,
    int UntrackedFiles
);
