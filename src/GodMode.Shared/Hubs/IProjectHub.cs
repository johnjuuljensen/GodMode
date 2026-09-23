using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Shared.Hubs;

/// <summary>
/// Interface for SignalR hub methods that clients can invoke on the server.
/// </summary>
public interface IProjectHub
{
    /// <summary>
    /// Lists all server-defined profiles.
    /// </summary>
    Task<ProfileInfo[]> ListProfiles();

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
    /// <param name="profileName">Name of the profile the root belongs to.</param>
    /// <param name="projectRootName">Name of the project root.</param>
    /// <param name="actionName">Name of the create action to use, or null for the default action.</param>
    /// <param name="inputs">Form inputs from the dynamic form.</param>
    Task<ProjectStatus> CreateProject(string profileName, string projectRootName, string? actionName, Dictionary<string, JsonElement> inputs);

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
    /// Deletes a project, running teardown scripts and removing all files.
    /// </summary>
    Task DeleteProject(string projectId, bool force = false);

    /// <summary>
    /// Archives a project (stops it, moves to archive, keeps data).
    /// </summary>
    Task ArchiveProject(string projectId);

    /// <summary>
    /// Restores an archived project.
    /// </summary>
    Task UnarchiveProject(string projectId);

    /// <summary>
    /// Lists all archived projects.
    /// </summary>
    Task<ProjectSummary[]> ListArchivedProjects();

    /// <summary>
    /// Creates a new profile with an optional description.
    /// </summary>
    Task CreateProfile(string name, string? description);

    /// <summary>
    /// Deletes a profile. When deleteContents is true, cascade-deletes all root directories
    /// and their projects; otherwise reassigns roots to the Default profile.
    /// </summary>
    Task DeleteProfile(string name, bool deleteContents = false);

    /// <summary>
    /// Updates a profile's description.
    /// </summary>
    Task UpdateProfileDescription(string name, string? description);

    // ── Utility ──

    /// <summary>
    /// Checks whether a CLI command is available on the server (in PATH).
    /// Returns the resolved path if found, null if not.
    /// </summary>
    Task<string?> CheckCommand(string command);
}
