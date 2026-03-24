using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.ClientBase.Abstractions;

/// <summary>
/// Represents a connection to a host for managing projects
/// </summary>
public interface IProjectConnection : IDisposable
{
    /// <summary>
    /// Gets whether the connection is currently active
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Lists all server-defined profiles.
    /// Returns empty for servers that don't support profiles.
    /// </summary>
    Task<IEnumerable<ProfileInfo>> ListProfilesAsync();

    /// <summary>
    /// Lists all available project roots with their input schemas
    /// </summary>
    Task<IEnumerable<ProjectRootInfo>> ListProjectRootsAsync();

    /// <summary>
    /// Lists all projects on the connected host
    /// </summary>
    Task<IEnumerable<ProjectSummary>> ListProjectsAsync();

    /// <summary>
    /// Gets the current status of a specific project
    /// </summary>
    Task<ProjectStatus> GetStatusAsync(string projectId);

    /// <summary>
    /// Creates a new project with the specified parameters
    /// </summary>
    /// <param name="profileName">Name of the profile the root belongs to.</param>
    /// <param name="projectRootName">Name of the project root where the project will be created.</param>
    /// <param name="actionName">Name of the create action to use, or null for the default action.</param>
    /// <param name="inputs">Form inputs as key-value pairs from the dynamic form.</param>
    Task<ProjectStatus> CreateProjectAsync(string profileName, string projectRootName, string? actionName, Dictionary<string, JsonElement> inputs);

    /// <summary>
    /// Sends user input to a project waiting for input
    /// </summary>
    Task SendInputAsync(string projectId, string input);

    /// <summary>
    /// Stops a running project
    /// </summary>
    Task StopProjectAsync(string projectId);

    /// <summary>
    /// Resumes a stopped project using its existing session
    /// </summary>
    Task ResumeProjectAsync(string projectId);

    /// <summary>
    /// Subscribes to output messages from a project, starting from a specific offset.
    /// Returns raw Claude JSON messages wrapped in ClaudeMessage for UI rendering.
    /// </summary>
    IObservable<ClaudeMessage> SubscribeOutput(string projectId, long fromOffset = 0);

    /// <summary>
    /// Gets the metrics HTML for a project
    /// </summary>
    Task<string> GetMetricsHtmlAsync(string projectId);

    /// <summary>
    /// Deletes a project, running teardown scripts and removing all files
    /// </summary>
    Task DeleteProjectAsync(string projectId, bool force = false);

    /// <summary>
    /// Event raised when the server streams creation progress for a project.
    /// Parameters: projectId, message.
    /// </summary>
    event Action<string, string>? CreationProgressReceived;

    /// <summary>
    /// Event raised when a new project is created (broadcast from server).
    /// </summary>
    event Action<ProjectStatus>? ProjectCreatedReceived;

    /// <summary>
    /// Event raised when a project is deleted (broadcast from server).
    /// </summary>
    event Action<string>? ProjectDeletedReceived;

    /// <summary>
    /// Disconnects from the host
    /// </summary>
    void Disconnect();

    // MCP Server Discovery & Configuration
    Task<McpRegistrySearchResult> SearchMcpServersAsync(string query, int pageSize = 20, int page = 1);
    Task<McpServerDetail?> GetMcpServerDetailAsync(string qualifiedName);
    Task AddMcpServerAsync(string serverName, McpServerConfig config, string targetLevel,
        string? profileName, string? rootName, string? actionName);
    Task RemoveMcpServerAsync(string serverName, string targetLevel,
        string? profileName, string? rootName, string? actionName);
    Task<Dictionary<string, McpServerConfig>> GetEffectiveMcpServersAsync(
        string profileName, string rootName, string? actionName);

    // Profile Management
    Task CreateProfileAsync(string profileName, string? description);
    Task UpdateProfileDescriptionAsync(string profileName, string? description);

    // Root Creation & Management
    Task<RootTemplate[]> ListRootTemplatesAsync();
    Task<RootPreview> PreviewRootFromTemplateAsync(string templateName, Dictionary<string, string> parameters);
    Task<RootPreview> GenerateRootWithLlmAsync(RootGenerationRequest request);
    Task CreateRootAsync(string profileName, string rootName, RootPreview preview);
    Task<RootPreview> GetRootPreviewAsync(string profileName, string rootName);
    Task UpdateRootAsync(string profileName, string rootName, RootPreview preview);

    // Root Sharing
    Task<byte[]> ExportRootAsync(string profileName, string rootName);
    Task<SharedRootPreview> PreviewImportFromBytesAsync(byte[] packageBytes);
    Task<SharedRootPreview> PreviewImportFromUrlAsync(string url);
    Task<SharedRootPreview> PreviewImportFromGitAsync(string repoUrl, string? subPath, string? gitRef);
    Task InstallSharedRootAsync(SharedRootPreview preview, string? localName);
    Task UninstallSharedRootAsync(string rootName);
}
