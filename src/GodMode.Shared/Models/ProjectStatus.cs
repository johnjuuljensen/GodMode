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
/// <param name="OutputOffset">The byte offset in output.jsonl after its last line: what a client that has all the output resumes from.</param>
/// <param name="RootName">The name of the project root this project belongs to.</param>
/// <param name="Model">The Claude model the session was started with. Used on resume so the session keeps running on the same model regardless of the current root config or machine default.</param>
/// <param name="RepoUrl">Deprecated. Kept for backward compatibility with existing status.json files on disk.</param>
/// <param name="PendingPermission">The tool call waiting for the user to allow or deny it, while the project is <see cref="ProjectState.WaitingPermission"/>. Null otherwise.</param>
/// <param name="PendingQuestion">The AskUserQuestion waiting for the user's answer, while the project is <see cref="ProjectState.WaitingInput"/> on it. Null otherwise.</param>
/// <param name="LastError">Why the project is in <see cref="ProjectState.Error"/>: the last lines claude wrote to stderr before it exited, or an error result's text. Null otherwise.</param>
/// <param name="PullRequest">The pull request the project's work became, as its root's <c>status</c> script last reported it. Null when there is none, or the root has no status script.</param>
/// <param name="StateAtShutdown">
/// What the project was doing when a server shutdown stopped it (<see cref="ProjectState.Running"/>,
/// <see cref="ProjectState.WaitingInput"/> or <see cref="ProjectState.WaitingPermission"/>), so the next
/// start carries on with it. Null when the project was not active then, was stopped by the user, or
/// has been launched or resumed since.
/// </param>
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
    string? RepoUrl = null,
    string? Model = null,
    string? LastError = null,
    PendingPermission? PendingPermission = null,
    PendingQuestion? PendingQuestion = null,
    string? LastResult = null,
    DateTime? LastResultAt = null,
    DateTime? QuestionAt = null,
    DateTime? SeenAt = null,
    PullRequestStatus? PullRequest = null,
    ProjectState? StateAtShutdown = null
);
