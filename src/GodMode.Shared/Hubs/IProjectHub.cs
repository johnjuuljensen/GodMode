using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Shared.Hubs;

/// <summary>
/// Interface for SignalR hub methods that clients can invoke on the server.
/// </summary>
public interface IProjectHub
{
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
    Task<ProjectDetail> CreateProject(string projectRootName, Dictionary<string, JsonElement> inputs);

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
    /// Subscribes to output events from a project.
    /// </summary>
    Task SubscribeProject(string projectId, long outputOffset);

    /// <summary>
    /// Unsubscribes from output events from a project.
    /// </summary>
    Task UnsubscribeProject(string projectId);

    /// <summary>
    /// Gets the metrics HTML for a project.
    /// </summary>
    Task<string> GetMetricsHtml(string projectId);
}
