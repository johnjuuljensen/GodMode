using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Interface for project management service
/// </summary>
public interface IProjectService
{
    Task<IEnumerable<ProjectRootInfo>> ListProjectRootsAsync(string profileName, string hostId);
    Task<IEnumerable<ProjectSummary>> ListProjectsAsync(string profileName, string hostId);
    Task<ProjectStatus> GetStatusAsync(string profileName, string hostId, string projectId, bool forceRefresh = false);
    Task<ProjectStatus> CreateProjectAsync(
        string profileName,
        string hostId,
        string serverProfileName,
        string projectRootName,
        string? actionName,
        Dictionary<string, JsonElement> inputs);
    Task SendInputAsync(string profileName, string hostId, string projectId, string input);
    Task StopProjectAsync(string profileName, string hostId, string projectId);
    Task ResumeProjectAsync(string profileName, string hostId, string projectId);
    Task DeleteProjectAsync(string profileName, string hostId, string projectId, bool force = false);
    Task<IObservable<ClaudeMessage>> SubscribeOutputAsync(string profileName, string hostId, string projectId, long fromOffset = 0);
    Task<string> GetMetricsHtmlAsync(string profileName, string hostId, string projectId);
    void ClearCache();
    void InvalidateProjectCache(string profileName, string hostId, string projectId);

    // MCP Server Discovery & Configuration
    Task<McpRegistrySearchResult> SearchMcpServersAsync(string profileName, string hostId, string query, int pageSize = 20, int page = 1);
    Task<McpServerDetail?> GetMcpServerDetailAsync(string profileName, string hostId, string qualifiedName);
    Task AddMcpServerAsync(string profileName, string hostId, string serverName, McpServerConfig config,
        string targetLevel, string? serverProfileName, string? rootName, string? actionName);
    Task RemoveMcpServerAsync(string profileName, string hostId, string serverName,
        string targetLevel, string? serverProfileName, string? rootName, string? actionName);
    Task<Dictionary<string, McpServerConfig>> GetEffectiveMcpServersAsync(
        string profileName, string hostId, string serverProfileName, string rootName, string? actionName);

    // Profile Management
    Task CreateProfileAsync(string profileName, string hostId, string newProfileName, string? description);
    Task UpdateProfileDescriptionAsync(string profileName, string hostId, string targetProfileName, string? description);
}
