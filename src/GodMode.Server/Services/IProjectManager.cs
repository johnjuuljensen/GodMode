using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Interface for managing projects and their lifecycle.
/// </summary>
public interface IProjectManager
{
    Task<ProjectSummary[]> ListProjectsAsync();
    Task<ProjectStatus> GetStatusAsync(string projectId);
    Task<ProjectDetail> CreateProjectAsync(CreateProjectRequest request);
    Task SendInputAsync(string projectId, string input);
    Task StopProjectAsync(string projectId);
    Task SubscribeProjectAsync(string projectId, long outputOffset, string connectionId);
    Task UnsubscribeProjectAsync(string projectId, string connectionId);
    Task<string> GetMetricsHtmlAsync(string projectId);
    Task CleanupConnectionAsync(string connectionId);
    Task RecoverProjectsAsync();
}
