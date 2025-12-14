using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Maui.Services;

/// <summary>
/// Interface for project management service
/// </summary>
public interface IProjectService
{
    Task<IEnumerable<ProjectRoot>> ListProjectRootsAsync(string profileName, string hostId);
    Task<IEnumerable<ProjectSummary>> ListProjectsAsync(string profileName, string hostId);
    Task<ProjectStatus> GetStatusAsync(string profileName, string hostId, string projectId, bool forceRefresh = false);
    Task<ProjectDetail> CreateProjectAsync(
        string profileName,
        string hostId,
        string name,
        string projectRootName,
        ProjectType projectType,
        string? repoUrl,
        string initialPrompt);
    Task SendInputAsync(string profileName, string hostId, string projectId, string input);
    Task StopProjectAsync(string profileName, string hostId, string projectId);
    Task<IObservable<OutputEvent>> SubscribeOutputAsync(string profileName, string hostId, string projectId, long fromOffset = 0);
    Task<string> GetMetricsHtmlAsync(string profileName, string hostId, string projectId);
    void ClearCache();
    void InvalidateProjectCache(string profileName, string hostId, string projectId);
}
