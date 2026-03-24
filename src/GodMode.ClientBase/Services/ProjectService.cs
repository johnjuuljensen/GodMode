using System.Text.Json;
using GodMode.ClientBase.Abstractions;
using GodMode.Shared.Models;
using System.Collections.Concurrent;

namespace GodMode.ClientBase.Services;

/// <summary>
/// High-level service for project operations with caching
/// </summary>
public class ProjectService : IProjectService
{
    private readonly IHostConnectionService _hostConnectionService;
    private readonly ConcurrentDictionary<string, ProjectStatus> _statusCache = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastStatusUpdate = new();
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromSeconds(30);

    public ProjectService(IHostConnectionService hostConnectionService)
    {
        _hostConnectionService = hostConnectionService;
    }

    public async Task<IEnumerable<ProjectRootInfo>> ListProjectRootsAsync(string profileName, string hostId)
    {
        var connection = await _hostConnectionService.ConnectToHostAsync(profileName, hostId);
        return await connection.ListProjectRootsAsync();
    }

    public async Task<IEnumerable<ProjectSummary>> ListProjectsAsync(string profileName, string hostId)
    {
        var connection = await _hostConnectionService.ConnectToHostAsync(profileName, hostId);
        return await connection.ListProjectsAsync();
    }

    public async Task<ProjectStatus> GetStatusAsync(string profileName, string hostId, string projectId, bool forceRefresh = false)
    {
        var cacheKey = $"{profileName}:{hostId}:{projectId}";

        // Check cache
        if (!forceRefresh &&
            _statusCache.TryGetValue(cacheKey, out var cachedStatus) &&
            _lastStatusUpdate.TryGetValue(cacheKey, out var lastUpdate) &&
            DateTime.UtcNow - lastUpdate < _cacheExpiration)
        {
            return cachedStatus;
        }

        // Fetch fresh status
        var connection = await _hostConnectionService.ConnectToHostAsync(profileName, hostId);
        var status = await connection.GetStatusAsync(projectId);

        // Update cache
        _statusCache[cacheKey] = status;
        _lastStatusUpdate[cacheKey] = DateTime.UtcNow;

        return status;
    }

    public async Task<ProjectStatus> CreateProjectAsync(
        string profileName,
        string hostId,
        string serverProfileName,
        string projectRootName,
        string? actionName,
        Dictionary<string, JsonElement> inputs)
    {
        var connection = await _hostConnectionService.ConnectToHostAsync(profileName, hostId);
        return await connection.CreateProjectAsync(serverProfileName, projectRootName, actionName, inputs);
    }

    public async Task SendInputAsync(string profileName, string hostId, string projectId, string input)
    {
        var connection = await _hostConnectionService.ConnectToHostAsync(profileName, hostId);
        await connection.SendInputAsync(projectId, input);

        // Invalidate cache
        var cacheKey = $"{profileName}:{hostId}:{projectId}";
        _statusCache.TryRemove(cacheKey, out _);
        _lastStatusUpdate.TryRemove(cacheKey, out _);
    }

    public async Task StopProjectAsync(string profileName, string hostId, string projectId)
    {
        var connection = await _hostConnectionService.ConnectToHostAsync(profileName, hostId);
        await connection.StopProjectAsync(projectId);

        // Invalidate cache
        var cacheKey = $"{profileName}:{hostId}:{projectId}";
        _statusCache.TryRemove(cacheKey, out _);
        _lastStatusUpdate.TryRemove(cacheKey, out _);
    }

    public async Task ResumeProjectAsync(string profileName, string hostId, string projectId)
    {
        var connection = await _hostConnectionService.ConnectToHostAsync(profileName, hostId);
        await connection.ResumeProjectAsync(projectId);

        // Invalidate cache
        var cacheKey = $"{profileName}:{hostId}:{projectId}";
        _statusCache.TryRemove(cacheKey, out _);
        _lastStatusUpdate.TryRemove(cacheKey, out _);
    }

    public async Task DeleteProjectAsync(string profileName, string hostId, string projectId, bool force = false)
    {
        var connection = await _hostConnectionService.ConnectToHostAsync(profileName, hostId);
        await connection.DeleteProjectAsync(projectId, force);

        // Invalidate cache
        var cacheKey = $"{profileName}:{hostId}:{projectId}";
        _statusCache.TryRemove(cacheKey, out _);
        _lastStatusUpdate.TryRemove(cacheKey, out _);
    }

    public async Task<IObservable<ClaudeMessage>> SubscribeOutputAsync(
        string profileName,
        string hostId,
        string projectId,
        long fromOffset = 0)
    {
        var connection = await _hostConnectionService.ConnectToHostAsync(profileName, hostId);
        return connection.SubscribeOutput(projectId, fromOffset);
    }

    public async Task<string> GetMetricsHtmlAsync(string profileName, string hostId, string projectId)
    {
        var connection = await _hostConnectionService.ConnectToHostAsync(profileName, hostId);
        return await connection.GetMetricsHtmlAsync(projectId);
    }

    public void ClearCache()
    {
        _statusCache.Clear();
        _lastStatusUpdate.Clear();
    }

    public void InvalidateProjectCache(string profileName, string hostId, string projectId)
    {
        var cacheKey = $"{profileName}:{hostId}:{projectId}";
        _statusCache.TryRemove(cacheKey, out _);
        _lastStatusUpdate.TryRemove(cacheKey, out _);
    }

    public async Task<McpRegistrySearchResult> SearchMcpServersAsync(string profileName, string hostId, string query, int pageSize = 20, int page = 1)
    {
        var connection = await _hostConnectionService.ConnectToHostAsync(profileName, hostId);
        return await connection.SearchMcpServersAsync(query, pageSize, page);
    }

    public async Task<McpServerDetail?> GetMcpServerDetailAsync(string profileName, string hostId, string qualifiedName)
    {
        var connection = await _hostConnectionService.ConnectToHostAsync(profileName, hostId);
        return await connection.GetMcpServerDetailAsync(qualifiedName);
    }

    public async Task AddMcpServerAsync(string profileName, string hostId, string serverName, McpServerConfig config,
        string targetLevel, string? serverProfileName, string? rootName, string? actionName)
    {
        var connection = await _hostConnectionService.ConnectToHostAsync(profileName, hostId);
        await connection.AddMcpServerAsync(serverName, config, targetLevel, serverProfileName, rootName, actionName);
    }

    public async Task RemoveMcpServerAsync(string profileName, string hostId, string serverName,
        string targetLevel, string? serverProfileName, string? rootName, string? actionName)
    {
        var connection = await _hostConnectionService.ConnectToHostAsync(profileName, hostId);
        await connection.RemoveMcpServerAsync(serverName, targetLevel, serverProfileName, rootName, actionName);
    }

    public async Task<Dictionary<string, McpServerConfig>> GetEffectiveMcpServersAsync(
        string profileName, string hostId, string serverProfileName, string rootName, string? actionName)
    {
        var connection = await _hostConnectionService.ConnectToHostAsync(profileName, hostId);
        return await connection.GetEffectiveMcpServersAsync(serverProfileName, rootName, actionName);
    }

    public async Task CreateProfileAsync(string profileName, string hostId, string newProfileName, string? description)
    {
        var connection = await _hostConnectionService.ConnectToHostAsync(profileName, hostId);
        await connection.CreateProfileAsync(newProfileName, description);
    }

    public async Task UpdateProfileDescriptionAsync(string profileName, string hostId, string targetProfileName, string? description)
    {
        var connection = await _hostConnectionService.ConnectToHostAsync(profileName, hostId);
        await connection.UpdateProfileDescriptionAsync(targetProfileName, description);
    }
}
