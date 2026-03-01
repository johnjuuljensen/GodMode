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

    public int ProcessId { get; set; }
    public CancellationTokenSource? ProcessCancellation { get; set; }

    public HashSet<string> SubscribedConnections { get; } = new();
}
