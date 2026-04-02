using GodMode.Shared.Models;

namespace GodMode.Shared.Hubs;

/// <summary>
/// Interface for SignalR client methods that the server can invoke.
/// Used with Hub&lt;IProjectHubClient&gt; for strongly-typed hub communication.
/// </summary>
public interface IProjectHubClient
{
    /// <summary>
    /// Called when the GodMode AI chat produces a response chunk.
    /// </summary>
    Task ChatResponse(ChatResponseMessage message);

    /// <summary>
    /// Called when output is received from a project's Claude process.
    /// The rawJson is the raw JSON line from Claude's --output-format stream-json.
    /// </summary>
    Task OutputReceived(string projectId, string rawJson);

    /// <summary>
    /// Called when a project's status changes.
    /// </summary>
    Task StatusChanged(string projectId, ProjectStatus status);

    /// <summary>
    /// Called when a new project is created.
    /// </summary>
    Task ProjectCreated(ProjectStatus status);

    /// <summary>
    /// Called during project creation to stream script progress to the client.
    /// </summary>
    Task CreationProgress(string projectId, string message);

    /// <summary>
    /// Called when a project is deleted.
    /// </summary>
    Task ProjectDeleted(string projectId);

    /// <summary>
    /// Called when roots change (created, updated, or deleted).
    /// Clients should refresh their root list.
    /// </summary>
    Task RootsChanged();

    /// <summary>
    /// Called when profiles change (created, updated, or deleted).
    /// Clients should refresh their profile list.
    /// </summary>
    Task ProfilesChanged();

    /// <summary>
    /// Called when webhooks change (created, updated, or deleted).
    /// Clients should refresh their webhook list.
    /// </summary>
    Task WebhooksChanged();
}
