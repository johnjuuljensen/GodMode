using GodMode.Server.Models;
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
    /// Archives a project (stops process, moves folder to .archived/).
    /// </summary>
    Task ArchiveProjectAsync(string projectId);

    /// <summary>
    /// Restores a project from archive.
    /// </summary>
    Task<ProjectSummary> UnarchiveProjectAsync(string projectId);

    /// <summary>
    /// Lists all archived projects.
    /// </summary>
    Task<ProjectSummary[]> ListArchivedProjectsAsync();

    /// <summary>
    /// Cleans up resources for a disconnected client.
    /// </summary>
    Task CleanupConnectionAsync(string connectionId);

    /// <summary>
    /// Recovers projects from disk on startup.
    /// </summary>
    Task RecoverProjectsAsync();

    /// <summary>
    /// Adds an MCP server at the specified level.
    /// </summary>
    Task AddMcpServerAsync(string serverName, McpServerConfig config, string targetLevel,
        string? profileName, string? rootName, string? actionName);

    /// <summary>
    /// Removes an MCP server at the specified level.
    /// </summary>
    Task RemoveMcpServerAsync(string serverName, string targetLevel,
        string? profileName, string? rootName, string? actionName);

    /// <summary>
    /// Gets effective MCP servers after three-level merge.
    /// </summary>
    Task<Dictionary<string, McpServerConfig>> GetEffectiveMcpServersAsync(
        string profileName, string rootName, string? actionName);

    /// <summary>
    /// Creates a new project root on disk.
    /// </summary>
    Task CreateRootAsync(string rootName, RootPreview preview, string? profileName);

    /// <summary>
    /// Deletes a project root from disk.
    /// </summary>
    Task DeleteRootAsync(string profileName, string rootName, bool force);

    /// <summary>
    /// Gets a preview of an existing root.
    /// </summary>
    Task<RootPreview?> GetRootPreviewAsync(string profileName, string rootName);

    /// <summary>
    /// Updates a root's .godmode-root/ contents.
    /// </summary>
    Task UpdateRootAsync(string profileName, string rootName, RootPreview preview);

    /// <summary>
    /// Creates a new profile and persists it to appsettings.json.
    /// </summary>
    Task CreateProfileAsync(string name, string? description);

    /// <summary>
    /// Deletes a profile. When deleteContents is true, cascade-deletes all root directories
    /// and their projects; otherwise reassigns roots to the Default profile.
    /// </summary>
    Task DeleteProfileAsync(string name, bool deleteContents = false);

    /// <summary>
    /// Updates a profile's description in appsettings.json.
    /// </summary>
    Task UpdateProfileDescriptionAsync(string name, string? description);

    // ── Webhooks ──

    Task<WebhookInfo[]> ListWebhooksAsync();
    Task<WebhookInfo> CreateWebhookAsync(string keyword, string profileName, string rootName,
        string? actionName = null, string? description = null,
        Dictionary<string, string>? inputMapping = null,
        Dictionary<string, System.Text.Json.JsonElement>? staticInputs = null);
    Task DeleteWebhookAsync(string keyword);
    Task<WebhookInfo> UpdateWebhookAsync(string keyword, string? description = null,
        Dictionary<string, string>? inputMapping = null,
        Dictionary<string, System.Text.Json.JsonElement>? staticInputs = null,
        bool? enabled = null);
    Task<string> RegenerateWebhookTokenAsync(string keyword);

    // ── Events ──

    /// <summary>
    /// Fired when a project transitions to Idle state (completed).
    /// The pipeline engine subscribes to this to advance pipeline stages.
    /// </summary>
    event Func<string, Task>? OnProjectCompleted;

    // ── Internal API (MCP bridge) ──

    ProjectInfo? ValidateProjectToken(string projectId, string token);
    Task StoreProjectResultAsync(string projectId, SubmitResultRequest resultRequest);
    Task UpdateCustomStatusAsync(string projectId, string message);
    Task RequestHumanReviewAsync(string projectId, RequestReviewRequest reviewRequest);

    Task<byte[]> ExportRootAsync(string profileName, string rootName);
    Task<SharedRootPreview> PreviewImportFromBytesAsync(byte[] packageBytes);
    Task<SharedRootPreview> PreviewImportFromUrlAsync(string url);
    Task<SharedRootPreview> PreviewImportFromGitAsync(string gitUrl, string? path, string? gitRef);
    Task InstallSharedRootAsync(string rootName, SharedRootPreview preview);
    Task UninstallSharedRootAsync(string rootName);
}
