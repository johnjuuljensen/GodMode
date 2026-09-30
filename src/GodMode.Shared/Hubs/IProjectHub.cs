using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Shared.Hubs;

/// <summary>
/// Interface for SignalR hub methods that clients can invoke on the server.
/// </summary>
public interface IProjectHub
{
    /// <summary>
    /// Lists all server-defined profiles.
    /// </summary>
    Task<ProfileInfo[]> ListProfiles();

    /// <summary>
    /// Lists all available project roots with their input schemas.
    /// </summary>
    Task<ProjectRootInfo[]> ListProjectRoots();

    /// <summary>
    /// Lists all projects.
    /// </summary>
    Task<ProjectSummary[]> ListProjects();

    /// <summary>
    /// Gets the status of a specific project.
    /// </summary>
    Task<ProjectStatus> GetStatus(string projectId);

    /// <summary>
    /// Creates a new project using config-driven workflow.
    /// </summary>
    /// <param name="profileName">Name of the profile the root belongs to.</param>
    /// <param name="projectRootName">Name of the project root.</param>
    /// <param name="actionName">Name of the create action to use, or null for the default action.</param>
    /// <param name="inputs">Form inputs from the dynamic form.</param>
    /// <returns>
    /// The project created; for an action that starts no session (<c>"session": false</c>), no project
    /// and the script's message. Progress comes as <c>CreationProgress</c> until this returns.
    /// </returns>
    Task<CreateProjectResult> CreateProject(string profileName, string projectRootName, string? actionName, Dictionary<string, JsonElement> inputs);

    /// <summary>
    /// Sends input to a project.
    /// </summary>
    Task SendInput(string projectId, string input);

    /// <summary>
    /// Stops a running project.
    /// </summary>
    Task StopProject(string projectId);

    /// <summary>
    /// Resumes a stopped project using its existing session.
    /// </summary>
    Task ResumeProject(string projectId);

    /// <summary>
    /// Answers the project's <see cref="ProjectStatus.PendingPermission"/>: the tool call runs, or
    /// claude is told it was denied. Fails when the project has no pending request with that id
    /// (it was answered already, or claude stopped waiting), and when another answer to it, from
    /// another client, came first: only one answer succeeds.
    /// </summary>
    Task RespondToPermission(string projectId, string requestId, PermissionDecision decision);

    /// <summary>
    /// Everything the project's pending permission request <paramref name="requestId"/> would run, to
    /// show before it is allowed. Fails as <see cref="RespondToPermission"/> does when the request is
    /// not pending, or is a question.
    /// </summary>
    Task<PermissionDetail> GetPermissionDetail(string projectId, string requestId);

    /// <summary>
    /// Answers the project's <see cref="ProjectStatus.PendingQuestion"/>. <paramref name="answers"/>
    /// maps each <see cref="QuestionItem.Question"/> to the chosen label (labels joined with ", " for
    /// a multi-select) or to the user's own text. Fails as <see cref="RespondToPermission"/> does.
    /// </summary>
    Task AnswerQuestion(string projectId, string requestId, Dictionary<string, string> answers);

    /// <summary>
    /// Every project on this server that needs the user, oldest <see cref="AttentionItem.Since"/>
    /// first, one item per project. <see cref="IProjectHubClient.AttentionChanged"/> pushes the
    /// same list whenever it changes.
    /// </summary>
    Task<AttentionItem[]> GetAttention();

    /// <summary>
    /// The user has seen the project's last result: it is no longer <see cref="Enums.AttentionKind.Finished"/>.
    /// Persisted, so it holds after a server restart. Other kinds are unaffected.
    /// </summary>
    Task MarkSeen(string projectId);

    /// <summary>
    /// Answers the project, whatever it is waiting on and whether or not its claude is running.
    /// With claude running this is <see cref="SendInput"/>: a pending permission request is denied
    /// with <paramref name="text"/> as the reason, a pending question with a single question is
    /// answered with it, and otherwise it is a new user turn. With claude not running the project is
    /// resumed with its session (as <see cref="ResumeProject"/>), sent <paramref name="text"/>, and
    /// the call returns once claude has reported its session started (<c>system/init</c>). It fails,
    /// saying why, when claude exits before that (the project is then Error, with its
    /// <see cref="ProjectStatus.LastError"/>) or does not report it within the server's timeout.
    /// </summary>
    Task ReplyAndResume(string projectId, string text);

    /// <summary>
    /// Subscribes to a project's output. The server replays output.jsonl from fromOffset in
    /// <see cref="IProjectHubClient.OutputBatch"/> messages, sends
    /// <see cref="IProjectHubClient.OutputReplayComplete"/>, and only then live lines, so each line
    /// arrives once and in order. fromOffset is the offset of the last line the client has (0 for
    /// everything; an offset inside a line snaps forward to the next line), or -N for the last N turns.
    /// </summary>
    /// <param name="subscriptionId">Made up by the client, and echoed by this subscription's batches
    /// and complete, so a client that has subscribed again since can tell an older one's answer.</param>
    /// <param name="generation">The output generation the client's offset is in (null when it holds
    /// none). A positive offset in any other generation is not in this file: all of it is replayed, from 0.</param>
    Task SubscribeProject(string projectId, long fromOffset, string subscriptionId, string? generation);

    /// <summary>
    /// Unsubscribes from output events from a project.
    /// </summary>
    Task UnsubscribeProject(string projectId);

    /// <summary>
    /// Deletes a project: stops it, runs its root's delete script (<paramref name="force"/> is passed
    /// to it as <c>GODMODE_FORCE</c>), which may refuse, and then removes its files. A session that
    /// shares its working folder loses only its state, moved to the folder's <c>.godmode/trash/</c>
    /// (<see cref="DeleteProjectResult.Trashed"/>); any other loses its working folder.
    /// </summary>
    Task<DeleteProjectResult> DeleteProject(string projectId, bool force = false);

    /// <summary>
    /// Undoes a delete that trashed the session (<see cref="DeleteProjectResult.Trashed"/>): its state
    /// goes back to <c>.godmode/sessions/</c> and it is tracked again, Stopped, under the same ID, pushed
    /// as <see cref="IProjectHubClient.ProjectCreated"/>. Its delete script is not undone. It fails, and
    /// changes nothing, when the session is not in the trash (purged, or never trashed), when its root
    /// is no longer listed under the profile and name in its ID (removed, or renamed), or when its
    /// folder no longer takes it.
    /// </summary>
    Task<ProjectStatus> RestoreProject(string projectId);

    // ── Adopting folders ──

    /// <summary>
    /// The folders of a root that GodMode does not manage, read now (nothing is cached): its <c>list</c>
    /// script's candidates, or, with none, its immediate subfolders but its own and the hidden ones. A
    /// folder a session works in (tracked, or with its state in <c>.godmode/sessions/</c>) is never one.
    /// Fails, saying why, when the root is not listed, or its script fails, times out or prints anything
    /// but the documented JSON.
    /// </summary>
    Task<UnmanagedFolder[]> ListUnmanaged(string profileName, string projectRootName);

    /// <summary>
    /// Makes a session of a folder that exists in the root (<see cref="UnmanagedFolder.Path"/>), as it is:
    /// no folder is made, and nothing in it is changed but its <c>.godmode/</c>. With an action that says
    /// <c>"adopt": true</c> its create script alone runs, with <c>GODMODE_ADOPT=true</c>, to name the
    /// session (<c>project_name</c>, <c>kind</c>, <c>project_prompt</c>); with any other action, or none,
    /// no script runs, and the session is <paramref name="inputs"/>' <c>name</c> (else the folder's) and
    /// <c>kind</c> (else the action's). With no prompt it starts idle, waiting for its first message.
    /// Refused, changing nothing, for a path that is not an immediate subfolder of the root, or a folder
    /// a session is in. Pushed as <see cref="IProjectHubClient.ProjectCreated"/>.
    /// </summary>
    /// <param name="actionName">The root's action the session is of (its environment, arguments and delete), or null for its first.</param>
    Task<ProjectStatus> AdoptFolder(string profileName, string projectRootName, string path, string? actionName, Dictionary<string, JsonElement>? inputs);

    /// <summary>
    /// Takes the session out of GodMode and leaves its folder be: it is stopped, no delete script runs,
    /// and only its state moves to the folder's <c>.godmode/trash/</c>, which
    /// <see cref="RestoreProject"/> brings back, as it was, until the trash is purged. Pushed as
    /// <see cref="IProjectHubClient.ProjectDeleted"/>.
    /// </summary>
    Task<DeleteProjectResult> ForgetProject(string projectId);

    // ── Utility ──

    /// <summary>
    /// Checks whether a CLI command is available on the server (in PATH).
    /// Returns the resolved path if found, null if not.
    /// </summary>
    Task<string?> CheckCommand(string command);
}
