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
    /// Per-project token for the GodMode MCP bridge to authenticate to internal API.
    /// Generated at creation time, passed as GODMODE_PROJECT_TOKEN env var.
    /// </summary>
    public string? ProjectToken { get; set; }

    /// <summary>
    /// Custom status message set via the MCP bridge (godmode_update_status).
    /// </summary>
    public string? CustomStatus { get; set; }

    public int ProcessId { get; set; }
    public CancellationTokenSource? ProcessCancellation { get; set; }

    public HashSet<string> SubscribedConnections { get; } = new();

    /// <summary>
    /// The most recent assistant text content block seen on the stream, used by
    /// the deterministic question detector to decide (on <c>result</c>) whether
    /// the turn ended with a question. Reset when a new turn starts.
    /// In-memory only; not persisted.
    /// </summary>
    public string? LastAssistantText { get; set; }
}

/// <summary>MCP bridge request to submit a project result.</summary>
public record SubmitResultRequest(object? Result, string? Summary);

/// <summary>MCP bridge request to update custom status.</summary>
public record UpdateStatusRequest(string Message);

/// <summary>MCP bridge request to ask for human review.</summary>
public record RequestReviewRequest(string Question, string? Context);

/// <summary>Structured result stored per project.</summary>
public record ProjectResult(object? Result, string? Summary, DateTime StoredAt);
