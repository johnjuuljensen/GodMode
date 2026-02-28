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
    private readonly IProfileService _profileService;
    private readonly ICredentialService _credentialService;
    private readonly IProjectServerMappingService _serverMappingService;
    private readonly ConcurrentDictionary<string, ProjectStatus> _statusCache = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastStatusUpdate = new();
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromSeconds(30);

    public ProjectService(
        IHostConnectionService hostConnectionService,
        IProfileService profileService,
        ICredentialService credentialService,
        IProjectServerMappingService serverMappingService)
    {
        _hostConnectionService = hostConnectionService;
        _profileService = profileService;
        _credentialService = credentialService;
        _serverMappingService = serverMappingService;
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

    public async Task<ProjectDetail> CreateProjectAsync(
        string profileName,
        string hostId,
        string? projectRootName,
        Dictionary<string, JsonElement> inputs,
        Dictionary<string, string>? environment = null)
    {
        var connection = await _hostConnectionService.ConnectToHostAsync(profileName, hostId);
        var detail = await connection.CreateProjectAsync(projectRootName, inputs, environment);

        // Save project→server mapping for credential injection on resume
        try
        {
            var serverId = await FindServerIdAsync(profileName, hostId);
            if (serverId != null)
                await _serverMappingService.SetServerIdAsync(detail.Status.Id, serverId);
        }
        catch { /* Non-critical */ }

        return detail;
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

    public async Task ResumeProjectAsync(string profileName, string hostId, string projectId, Dictionary<string, string>? environment = null)
    {
        // Auto-resolve credentials if none provided
        environment ??= await ResolveProjectEnvironmentAsync(projectId, profileName);

        var connection = await _hostConnectionService.ConnectToHostAsync(profileName, hostId);
        await connection.ResumeProjectAsync(projectId, environment);

        // Invalidate cache
        var cacheKey = $"{profileName}:{hostId}:{projectId}";
        _statusCache.TryRemove(cacheKey, out _);
        _lastStatusUpdate.TryRemove(cacheKey, out _);
    }

    public async Task DeleteProjectAsync(string profileName, string hostId, string projectId)
    {
        var connection = await _hostConnectionService.ConnectToHostAsync(profileName, hostId);
        await connection.DeleteProjectAsync(projectId);

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

    public async Task<IEnumerable<RepoInfo>> ListKnownReposAsync(string profileName, string hostId)
    {
        var connection = await _hostConnectionService.ConnectToHostAsync(profileName, hostId);
        return await connection.ListKnownReposAsync();
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

    /// <summary>
    /// Resolves environment variables for a project based on its server's system mappings.
    /// </summary>
    private async Task<Dictionary<string, string>?> ResolveProjectEnvironmentAsync(string projectId, string profileName)
    {
        try
        {
            var serverId = await _serverMappingService.GetServerIdAsync(projectId);
            if (serverId == null) return null;

            var profile = await _profileService.GetProfileAsync(profileName);
            var server = profile?.Servers.FirstOrDefault(s => s.Id == serverId);
            if (server?.Systems is not { Count: > 0 }) return null;

            var credentialNames = server.Systems.Values.Distinct();
            var resolved = await _credentialService.ResolveCredentialsAsync(profileName, credentialNames);

            var env = new Dictionary<string, string>();
            foreach (var (systemName, credentialName) in server.Systems)
            {
                if (resolved.TryGetValue(credentialName, out var value))
                {
                    env[SystemEnvMapper.GetEnvVarName(systemName)] = value;
                }
            }

            return env.Count > 0 ? env : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the server ID for a given host by matching host connection info to profile servers.
    /// </summary>
    private async Task<string?> FindServerIdAsync(string profileName, string hostId)
    {
        try
        {
            var profile = await _profileService.GetProfileAsync(profileName);
            // The hostId from connections maps to the server's Id in the profile
            return profile?.Servers.FirstOrDefault(s => s.Id == hostId)?.Id
                ?? profile?.Servers.FirstOrDefault()?.Id;
        }
        catch
        {
            return null;
        }
    }
}
