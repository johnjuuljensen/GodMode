using GodMode.Shared.Models;

namespace GodMode.Shared.Hubs;

/// <summary>
/// Interface for SignalR client methods that the server can invoke.
/// Used with Hub&lt;IProjectHubClient&gt; for strongly-typed hub communication.
/// </summary>
public interface IProjectHubClient
{
    /// <summary>
    /// Called when output is received from a project's Claude process.
    /// </summary>
    Task OutputReceived(string projectId, OutputEvent outputEvent);

    /// <summary>
    /// Called when a project's status changes.
    /// </summary>
    Task StatusChanged(string projectId, ProjectStatus status);

    /// <summary>
    /// Called when a new project is created.
    /// </summary>
    Task ProjectCreated(ProjectStatus status);
}
