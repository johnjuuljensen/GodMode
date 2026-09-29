using GodMode.Shared.Models;

namespace GodMode.Server.Models;

/// <summary>
/// Internal project information tracked by the server.
/// Composes a <see cref="ProjectStatus"/> for all client-visible state,
/// and adds server-internal fields (process management, subscriptions).
/// </summary>
public class ProjectInfo
{
    public required ProjectStatus Status { get; set; }

    /// <summary>
    /// The working folder: Claude's working directory, where the session's state is too (<see cref="StatePath"/>).
    /// May be updated after create scripts run (scripts can override via result file).
    /// </summary>
    public required string ProjectPath { get; set; }

    /// <summary>
    /// The folder (full path) of the root the session is in, as it was when the session was created or
    /// recovered. Its config and scripts are read there, whatever the root is called now: a root name
    /// that comes to name another folder (a new explicit root that wins the clash) is not this root.
    /// </summary>
    public required string RootPath { get; init; }

    /// <summary>
    /// The session's id, <c>yymmdd-{kind}-{slug}-{suffix}</c> (<see cref="ProjectFiles.SessionState"/>): the
    /// last part of its opaque ID, and the name of its state folder. Unique within its root.
    /// </summary>
    public required string SessionId { get; set; }

    /// <summary>The session's state folder, <c>{ProjectPath}/.godmode/sessions/{SessionId}/</c>: status.json, output.jsonl and the rest.</summary>
    public string StatePath => ProjectFiles.SessionState.PathOf(ProjectPath, SessionId);

    /// <summary>
    /// Whether the session shares its working folder with others (its action's <c>sharedFolder</c>,
    /// kept in its settings.json): its delete removes only <see cref="StatePath"/>, never the folder.
    /// </summary>
    public bool SharedFolder { get; set; }

    /// <summary>
    /// claude's session GUID (<c>--session-id</c>, <c>--resume</c>), kept in <c>session-id</c> in the state folder.
    /// Not the session's id: GodMode replaces it when a resume finds no conversation.
    /// </summary>
    public string? ClaudeSessionId { get; set; }

    /// <summary>
    /// The name of the create action used to create this project.
    /// Used to look up action-specific config for teardown and resume.
    /// </summary>
    public string? ActionName { get; set; }

    /// <summary>
    /// The profile this project belongs to.
    /// Set at creation time and during recovery.
    /// </summary>
    public string? ProfileName { get; set; }

    /// <summary>
    /// The token the project's claude calls GodMode's MCP endpoint with. Issued afresh for every
    /// launch, and handed to claude only in its MCP config file.
    /// </summary>
    public string? ProjectToken { get; set; }

    /// <summary>The process, its output pipeline and the locks that order changes to <see cref="Status"/>.</summary>
    public ProjectProcess Process { get; } = new();

    public HashSet<string> SubscribedConnections { get; } = new();
}
