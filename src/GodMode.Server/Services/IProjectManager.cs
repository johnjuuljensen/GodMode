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
    /// Gets the metrics HTML for a project.
    /// </summary>
    Task<string> GetMetricsHtmlAsync(string projectId);

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
    /// Creates a new profile and persists it to appsettings.json.
    /// </summary>
    Task CreateProfileAsync(string name, string? description);

    /// <summary>
    /// Updates a profile's description in appsettings.json.
    /// </summary>
    Task UpdateProfileDescriptionAsync(string name, string? description);
}
