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
    /// <param name="projectRootName">Name of the project root (null for workspace-less projects).</param>
    /// <param name="inputs">Dynamic form inputs.</param>
    /// <param name="environment">Optional client-injected environment variables (e.g. resolved credentials).</param>
    Task<ProjectDetail> CreateProject(string? projectRootName, Dictionary<string, JsonElement> inputs, Dictionary<string, string>? environment = null);

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
    /// <param name="projectId">The project to resume.</param>
    /// <param name="environment">Optional client-injected environment variables (e.g. resolved credentials).</param>
    Task ResumeProject(string projectId, Dictionary<string, string>? environment = null);

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

    /// <summary>
    /// Lists known repositories configured on the server.
    /// </summary>
    Task<RepoInfo[]> ListKnownRepos();

    /// <summary>
    /// Deletes a project, running teardown scripts and removing all files.
    /// </summary>
    Task DeleteProject(string projectId);
}
