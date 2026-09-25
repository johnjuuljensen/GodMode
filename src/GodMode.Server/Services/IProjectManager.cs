using GodMode.Server.Models;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Interface for managing projects and their lifecycle.
/// </summary>
public interface IProjectManager
{
    /// <summary>
    /// Lists all server-defined profiles.
    /// </summary>
    Task<ProfileInfo[]> ListProfilesAsync();

    /// <summary>
    /// Lists all available project roots with their input schemas.
    /// </summary>
    Task<ProjectRootInfo[]> ListProjectRootsAsync();

    /// <summary>
    /// Lists all projects across all project roots.
    /// </summary>
    Task<ProjectSummary[]> ListProjectsAsync();

    /// <summary>
    /// Gets the status of a specific project.
    /// </summary>
    Task<ProjectStatus> GetStatusAsync(string projectId);

    /// <summary>
    /// Creates a new project using the config-driven workflow.
    /// </summary>
    Task<ProjectStatus> CreateProjectAsync(CreateProjectRequest request);

    /// <summary>
    /// Sends input to a running project.
    /// </summary>
    Task SendInputAsync(string projectId, string input);

    /// <summary>
    /// Answers the project whether its claude runs or not (hub ReplyAndResume): input to a running
    /// one, else a resume, the input, and a wait for claude to report its session started.
    /// </summary>
    Task ReplyAndResumeAsync(string projectId, string text);

    /// <summary>Every project that needs the user, oldest first (hub GetAttention).</summary>
    AttentionItem[] GetAttention();

    /// <summary>The user has seen the project's last result (hub MarkSeen).</summary>
    Task MarkSeenAsync(string projectId);

    /// <summary>Answers the project's pending permission prompt <paramref name="requestId"/> (hub RespondToPermission).</summary>
    Task RespondToPermissionAsync(string projectId, string requestId, PermissionDecision decision);

    /// <summary>Answers the project's pending question <paramref name="requestId"/> (hub AnswerQuestion).</summary>
    Task AnswerQuestionAsync(string projectId, string requestId, IReadOnlyDictionary<string, string> answers);

    /// <summary>
    /// Stops a running project.
    /// </summary>
    Task StopProjectAsync(string projectId);

    /// <summary>
    /// Resumes a stopped project using its existing session.
    /// </summary>
    Task ResumeProjectAsync(string projectId);

    /// <summary>
    /// Subscribes a client connection to project output.
    /// </summary>
    Task SubscribeProjectAsync(string projectId, long outputOffset, string connectionId);

    /// <summary>
    /// Unsubscribes a client connection from project output.
    /// </summary>
    Task UnsubscribeProjectAsync(string projectId, string connectionId);

    /// <summary>
    /// Deletes a project, running teardown scripts and removing all files.
    /// </summary>
    Task DeleteProjectAsync(string projectId, bool force = false);

    /// <summary>
    /// Cleans up resources for a disconnected client.
    /// </summary>
    Task CleanupConnectionAsync(string connectionId);

    /// <summary>
    /// Recovers projects from disk on startup.
    /// </summary>
    Task RecoverProjectsAsync();

    /// <summary>
    /// After recovery, once the server is listening: carries on with every project the last
    /// shutdown stopped while it was active (<see cref="ProjectStatus.StateAtShutdown"/>), as its
    /// root's <c>resumeOnRestart</c> and <c>resumePrompt</c> say.
    /// </summary>
    Task ResumeInterruptedProjectsAsync();

    // ── Events ──

    /// <summary>
    /// Fired when a project transitions to Idle state (completed).
    /// The pipeline engine subscribes to this to advance pipeline stages.
    /// </summary>
    event Func<string, Task>? OnProjectCompleted;

    // ── Internal API (MCP bridge) ──

    ProjectInfo? ValidateProjectToken(string projectId, string token);
    Task StoreProjectResultAsync(string projectId, SubmitResultRequest resultRequest);
    Task UpdateCustomStatusAsync(string projectId, string message);
    Task RequestHumanReviewAsync(string projectId, RequestReviewRequest reviewRequest);

    /// <summary>
    /// The bridge's permission_prompt: waits until the user answers, and returns what claude gets.
    /// Canceled by <paramref name="aborted"/> when the bridge's call goes away.
    /// </summary>
    Task<PermissionPromptResult> RequestPermissionAsync(string projectId, PermissionPromptRequest request, CancellationToken aborted);
}
