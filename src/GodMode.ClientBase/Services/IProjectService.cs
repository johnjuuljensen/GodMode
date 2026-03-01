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
    Task<ProjectDetail> CreateProjectAsync(
        string profileName,
        string hostId,
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
}
