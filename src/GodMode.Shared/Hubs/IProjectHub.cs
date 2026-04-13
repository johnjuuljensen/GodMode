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

    /// <summary>
    /// Exports a root as a .gmroot ZIP package.
    /// </summary>
    Task<byte[]> ExportRoot(string profileName, string rootName);

    /// <summary>
    /// Previews a .gmroot package from raw bytes before installation.
    /// </summary>
    Task<SharedRootPreview> PreviewImportFromBytes(byte[] packageBytes);

    /// <summary>
    /// Previews a .gmroot package from a URL before installation.
    /// </summary>
    Task<SharedRootPreview> PreviewImportFromUrl(string url);

    /// <summary>
    /// Previews a root from a git repo before installation.
    /// </summary>
    Task<SharedRootPreview> PreviewImportFromGit(string gitUrl, string? path = null, string? gitRef = null);

    /// <summary>
    /// Installs a previously previewed shared root.
    /// </summary>
    Task InstallSharedRoot(string rootName, SharedRootPreview preview);

    /// <summary>
    /// Uninstalls a shared root.
    /// </summary>
    Task UninstallSharedRoot(string rootName);

    /// <summary>
    /// Applies a manifest to converge the server to the declared state.
    /// </summary>
    Task<ConvergenceResult> ApplyManifest(string manifestContent, bool force = false);

    /// <summary>
    /// Exports the current server state as a manifest JSON string.
    /// </summary>
    Task<string> ExportManifest();

    /// <summary>
    /// Generates a root configuration using LLM inference from a natural language instruction.
    /// </summary>
    Task<RootPreview> GenerateRootWithLlm(RootGenerationRequest request);

    /// <summary>
    /// Sends a message to the GodMode AI chat (meta-management).
    /// </summary>
    Task SendChatMessage(string message);

    /// <summary>
    /// Clears the GodMode chat history for this connection.
    /// </summary>
    Task ClearChatHistory();

    // ── OAuth ──

    /// <summary>
    /// Gets the OAuth connection status for all providers in a profile.
    /// </summary>
    Task<Dictionary<string, OAuthProviderStatus>> GetOAuthStatus(string profileName);

    /// <summary>
    /// Disconnects (deletes tokens for) an OAuth provider in a profile.
    /// </summary>
    Task DisconnectOAuthProvider(string profileName, string provider);

    // ── Webhooks ──

    /// <summary>
    /// Lists all configured webhooks (tokens redacted).
    /// </summary>
    Task<WebhookInfo[]> ListWebhooks();

    /// <summary>
    /// Creates a new webhook. Returns the info including the full token (shown once).
    /// </summary>
    Task<WebhookInfo> CreateWebhook(string keyword, string profileName, string rootName,
        string? actionName = null, string? description = null,
        Dictionary<string, string>? inputMapping = null,
        Dictionary<string, JsonElement>? staticInputs = null);

    /// <summary>
    /// Deletes a webhook.
    /// </summary>
    Task DeleteWebhook(string keyword);

    /// <summary>
    /// Updates webhook settings (preserves token).
    /// </summary>
    Task<WebhookInfo> UpdateWebhook(string keyword, string? description = null,
        Dictionary<string, string>? inputMapping = null,
        Dictionary<string, JsonElement>? staticInputs = null,
        bool? enabled = null);

    /// <summary>
    /// Regenerates the webhook token. Returns the new full token (shown once).
    /// </summary>
    Task<string> RegenerateWebhookToken(string keyword);

    // ── Utility ──

    /// <summary>
    /// Checks whether a CLI command is available on the server (in PATH).
    /// Returns the resolved path if found, null if not.
    /// </summary>
    Task<string?> CheckCommand(string command);

    // ── Schedules ──

    /// <summary>
    /// Lists all schedules for a profile.
    /// </summary>
    Task<ScheduleInfo[]> GetSchedules(string profileName);

    /// <summary>
    /// Creates a new schedule in a profile.
    /// </summary>
    Task<ScheduleInfo> CreateSchedule(string profileName, string name, ScheduleConfig config);

    /// <summary>
    /// Updates an existing schedule.
    /// </summary>
    Task<ScheduleInfo> UpdateSchedule(string profileName, string name, ScheduleConfig config);

    /// <summary>
    /// Deletes a schedule.
    /// </summary>
    Task DeleteSchedule(string profileName, string name);

    /// <summary>
    /// Toggles a schedule's enabled state.
    /// </summary>
    Task<ScheduleInfo> ToggleSchedule(string profileName, string name, bool enabled);

    // ── Storage Browser ──

    /// <summary>
    /// Lists files and directories at a path relative to ProjectRootsDir.
    /// </summary>
    Task<StorageEntry[]> BrowseStorage(string path);

    /// <summary>
    /// Reads a file's content (text only, max 1MB).
    /// </summary>
    Task<string> ReadStorageFile(string path);

    /// <summary>
    /// Writes content to a file (creates parent dirs if needed).
    /// </summary>
    Task WriteStorageFile(string path, string content);

    /// <summary>
    /// Deletes a file or empty directory.
    /// </summary>
    Task DeleteStorageEntry(string path);

    /// <summary>
    /// Creates a directory.
    /// </summary>
    Task CreateStorageDirectory(string path);
}
