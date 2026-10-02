using GodMode.Shared.Enums;

namespace GodMode.Shared.Models;

/// <summary>
/// Detailed status information about a project.
/// </summary>
/// <param name="Id">The session's identifier, <c>{profile}/{root}/{id}</c>, with its id (<c>260929-feat-left-list-k7q2</c>), the name of its state folder, <c>.godmode/sessions/{id}/</c>. Opaque to clients, which pass it back as received; not the folder name.</param>
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
/// <param name="Effort">The claude effort level the session was started with, kept and used on resume as <paramref name="Model"/> is. Empty when it was started at claude's own default; null only for a session created before effort was kept, which takes its action's.</param>
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
/// <param name="Kind">What kind of session it is (<c>bug</c>, <c>feat</c>, <c>experiment</c>, <c>chat</c>…): its create script's <c>kind</c>, else its action's name, as its id has it (lowercase <c>[a-z0-9-]</c>). The app labels the session with it.</param>
/// <param name="ActionName">The action the session was created with, as its <c>settings.json</c> says: the app finds the action's <see cref="CreateActionInfo.Transient"/> by it.</param>
/// <param name="SharedFolder">
/// Whether the session shares its working folder (its <c>settings.json</c>'s <c>sharedFolder</c>, or one
/// that cannot be read): its delete removes only its state, into the folder's trash, and
/// <see cref="Hubs.IProjectHub.RestoreProject"/> can bring it back. Otherwise the delete removes the working folder.
/// </param>
/// <param name="Adopted">
/// Whether the session was adopted (<see cref="Hubs.IProjectHub.AdoptFolder"/>, its <c>settings.json</c>'s
/// <c>adopted</c>): its folder was there before it, so the app offers <see cref="Hubs.IProjectHub.ForgetProject"/>
/// beside its delete, which keeps the folder.
/// </param>
/// <param name="ParentId">
/// The <see cref="Id"/> of the session that started this one, on the same server, set at its create and
/// kept for its life; null for a top-level session. Metadata only: the parent's stop or delete leaves its
/// children as they are, so it may name a session that is gone, which the app shows as top level.
/// </param>
/// <param name="SpokenSummary">
/// The session's own spoken version of its last turn's reply: the text of the <c>speak</c> call that turn made in its
/// main conversation, which the server accepted (issue #384). Set with <paramref name="LastResult"/> as the turn ends,
/// null for a turn that made none or ended in error, and cleared as the next turn starts. Plain text of at most about
/// 300 characters, for voice to say word for word; the whole reply stays in the transcript.
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
    string? Model = null,
    string? LastError = null,
    PendingPermission? PendingPermission = null,
    PendingQuestion? PendingQuestion = null,
    string? LastResult = null,
    DateTime? LastResultAt = null,
    DateTime? QuestionAt = null,
    DateTime? SeenAt = null,
    PullRequestStatus? PullRequest = null,
    ProjectState? StateAtShutdown = null,
    string? Kind = null,
    string? ActionName = null,
    bool SharedFolder = false,
    bool Adopted = false,
    string? Effort = null,
    string? ParentId = null,
    string? SpokenSummary = null
);
