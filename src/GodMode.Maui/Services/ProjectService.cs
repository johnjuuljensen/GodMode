using GodMode.Maui.Abstractions;
using GodMode.Shared.Models;
using GodMode.Shared.Enums;
using System.Collections.Concurrent;

namespace GodMode.Maui.Services;

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

    public async Task<ProjectDetail> CreateProjectAsync(
        string profileName,
        string hostId,
        string name,
        string? repoUrl,
        string initialPrompt)
    {
        var connection = await _hostConnectionService.ConnectToHostAsync(profileName, hostId);
        return await connection.CreateProjectAsync(name, repoUrl, initialPrompt);
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

    public async Task<IObservable<OutputEvent>> SubscribeOutputAsync(
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
}
