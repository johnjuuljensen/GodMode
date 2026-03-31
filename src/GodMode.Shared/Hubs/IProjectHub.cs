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
    /// Gets the metrics HTML for a project.
    /// </summary>
    Task<string> GetMetricsHtml(string projectId);

    /// <summary>
    /// Deletes a project, running teardown scripts and removing all files.
    /// </summary>
    Task DeleteProject(string projectId, bool force = false);

    /// <summary>
    /// Adds an MCP server at the specified level (profile, root, or action).
    /// </summary>
    /// <param name="serverName">MCP server name.</param>
    /// <param name="config">MCP server configuration.</param>
    /// <param name="targetLevel">Target level: "profile", "root", or "action".</param>
    /// <param name="profileName">Required for profile-level writes.</param>
    /// <param name="rootName">Required for root/action-level writes.</param>
    /// <param name="actionName">Required for action-level writes.</param>
    Task AddMcpServer(string serverName, McpServerConfig config, string targetLevel,
        string? profileName = null, string? rootName = null, string? actionName = null);

    /// <summary>
    /// Removes an MCP server at the specified level.
    /// </summary>
    Task RemoveMcpServer(string serverName, string targetLevel,
        string? profileName = null, string? rootName = null, string? actionName = null);

    /// <summary>
    /// Gets the effective MCP servers for a given profile/root/action combination
    /// after three-level merge (profile -> root -> action).
    /// </summary>
    Task<Dictionary<string, McpServerConfig>> GetEffectiveMcpServers(
        string profileName, string rootName, string? actionName = null);

    /// <summary>
    /// Creates a new project root on disk with the given files.
    /// </summary>
    /// <param name="rootName">Name for the root directory.</param>
    /// <param name="preview">File contents to write into .godmode-root/.</param>
    /// <param name="profileName">Optional profile to associate with (for autodiscovery).</param>
    Task CreateRoot(string rootName, RootPreview preview, string? profileName = null);

    /// <summary>
    /// Deletes a project root from disk.
    /// </summary>
    Task DeleteRoot(string profileName, string rootName, bool force = false);

    /// <summary>
    /// Gets a preview of an existing root's .godmode-root/ contents.
    /// </summary>
    Task<RootPreview?> GetRootPreview(string profileName, string rootName);

    /// <summary>
    /// Updates a root by overwriting its .godmode-root/ contents.
    /// </summary>
    Task UpdateRoot(string profileName, string rootName, RootPreview preview);

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
}
