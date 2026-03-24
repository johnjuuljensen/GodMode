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

    // MCP Server Discovery & Configuration

    /// <summary>
    /// Searches the Smithery MCP server registry.
    /// </summary>
    Task<McpRegistrySearchResult> SearchMcpServers(string query, int pageSize = 20, int page = 1);

    /// <summary>
    /// Gets full detail for a specific MCP server by qualified name.
    /// </summary>
    Task<McpServerDetail?> GetMcpServerDetail(string qualifiedName);

    /// <summary>
    /// Adds an MCP server configuration at the specified level (profile, root, or action).
    /// </summary>
    Task AddMcpServer(string serverName, McpServerConfig config, string targetLevel,
        string? profileName = null, string? rootName = null, string? actionName = null);

    /// <summary>
    /// Removes an MCP server from the specified level.
    /// </summary>
    Task RemoveMcpServer(string serverName, string targetLevel,
        string? profileName = null, string? rootName = null, string? actionName = null);

    /// <summary>
    /// Gets the effective (merged) MCP servers for a given profile/root/action combination.
    /// </summary>
    Task<Dictionary<string, McpServerConfig>> GetEffectiveMcpServers(
        string profileName, string rootName, string? actionName = null);

    // Profile Management

    /// <summary>
    /// Creates a new profile with optional description.
    /// </summary>
    Task CreateProfile(string profileName, string? description = null);

    /// <summary>
    /// Updates a profile's description.
    /// </summary>
    Task UpdateProfileDescription(string profileName, string? description);

    // Root Creation & Management

    /// <summary>Lists available root templates.</summary>
    Task<RootTemplate[]> ListRootTemplates();

    /// <summary>Instantiates a template with parameters, returns preview.</summary>
    Task<RootPreview> PreviewRootFromTemplate(string templateName, Dictionary<string, string> parameters);

    /// <summary>Generates or modifies root files using LLM assistance.</summary>
    Task<RootPreview> GenerateRootWithLlm(RootGenerationRequest request);

    /// <summary>Creates a new root from a preview.</summary>
    Task CreateRoot(string profileName, string rootName, RootPreview preview);

    /// <summary>Gets an existing root's files as a preview for editing.</summary>
    Task<RootPreview> GetRootPreview(string profileName, string rootName);

    /// <summary>Updates an existing root from a preview.</summary>
    Task UpdateRoot(string profileName, string rootName, RootPreview preview);

    // Root Sharing (Export/Import)

    /// <summary>Exports a root as a .gmroot package (ZIP bytes).</summary>
    Task<byte[]> ExportRoot(string profileName, string rootName);

    /// <summary>Previews a root from uploaded package bytes.</summary>
    Task<SharedRootPreview> PreviewImportFromBytes(byte[] packageBytes);

    /// <summary>Previews a root from a URL pointing to a .gmroot file.</summary>
    Task<SharedRootPreview> PreviewImportFromUrl(string url);

    /// <summary>Previews a root from a git repository.</summary>
    Task<SharedRootPreview> PreviewImportFromGit(string repoUrl, string? subPath, string? gitRef);

    /// <summary>Installs a previously previewed shared root.</summary>
    Task InstallSharedRoot(SharedRootPreview preview, string? localName);

    /// <summary>Uninstalls a shared root.</summary>
    Task UninstallSharedRoot(string rootName);

    // Server Management

    /// <summary>Checks if inference (LLM) is configured and available.</summary>
    Task<InferenceStatus> GetInferenceStatus();

    /// <summary>Saves an Anthropic API key and reinitializes inference.</summary>
    Task<InferenceStatus> ConfigureInferenceApiKey(string apiKey);

    /// <summary>Restarts the server process.</summary>
    Task RestartServer();
}
