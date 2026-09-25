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
    /// The project directory path. This is also the working directory for Claude.
    /// May be updated after create scripts run (scripts can override via result file).
    /// </summary>
    public required string ProjectPath { get; set; }

    public string? SessionId { get; set; }

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
