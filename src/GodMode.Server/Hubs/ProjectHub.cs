using System.Text.Json;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using GodMode.Server.Services;
using Microsoft.AspNetCore.SignalR;

namespace GodMode.Server.Hubs;

/// <summary>
/// SignalR hub for real-time project communication.
/// </summary>
public class ProjectHub : Hub<IProjectHubClient>, IProjectHub
{
    private readonly IProjectManager _projectManager;
    private readonly ILogger<ProjectHub> _logger;

    public ProjectHub(IProjectManager projectManager, ILogger<ProjectHub> logger)
    {
        _projectManager = projectManager;
        _logger = logger;
    }

    public async Task<ProjectRootInfo[]> ListProjectRoots()
    {
        _logger.LogInformation("Client {ConnectionId} requested project roots", Context.ConnectionId);
        return await _projectManager.ListProjectRootsAsync();
    }

    public async Task<ProjectSummary[]> ListProjects()
    {
        _logger.LogInformation("Client {ConnectionId} requested project list", Context.ConnectionId);
        return await _projectManager.ListProjectsAsync();
    }

    public async Task<ProjectStatus> GetStatus(string projectId)
    {
        _logger.LogInformation("Client {ConnectionId} requested status for project {ProjectId}",
            Context.ConnectionId, projectId);
        return await _projectManager.GetStatusAsync(projectId);
    }

    public async Task<ProjectDetail> CreateProject(string projectRootName, Dictionary<string, JsonElement> inputs)
    {
        _logger.LogInformation("Client {ConnectionId} creating project in root '{Root}' with {InputCount} inputs",
            Context.ConnectionId, projectRootName, inputs.Count);

        var request = new CreateProjectRequest(projectRootName, inputs);
        var project = await _projectManager.CreateProjectAsync(request);

        // Notify all clients about the new project
        await Clients.All.ProjectCreated(project.Status);

        return project;
    }

    public async Task SendInput(string projectId, string input)
    {
        _logger.LogInformation("Client {ConnectionId} sending input to project {ProjectId}",
            Context.ConnectionId, projectId);
        await _projectManager.SendInputAsync(projectId, input);
    }

    public async Task StopProject(string projectId)
    {
        _logger.LogInformation("Client {ConnectionId} stopping project {ProjectId}",
            Context.ConnectionId, projectId);
        await _projectManager.StopProjectAsync(projectId);
    }

    public async Task ResumeProject(string projectId)
    {
        _logger.LogInformation("Client {ConnectionId} resuming project {ProjectId}",
            Context.ConnectionId, projectId);
        await _projectManager.ResumeProjectAsync(projectId);
    }

    public async Task SubscribeProject(string projectId, long outputOffset)
    {
        _logger.LogInformation("Client {ConnectionId} subscribing to project {ProjectId} from offset {Offset}",
            Context.ConnectionId, projectId, outputOffset);

        await Groups.AddToGroupAsync(Context.ConnectionId, $"project-{projectId}");
        await _projectManager.SubscribeProjectAsync(projectId, outputOffset, Context.ConnectionId);
    }

    public async Task UnsubscribeProject(string projectId)
    {
        _logger.LogInformation("Client {ConnectionId} unsubscribing from project {ProjectId}",
            Context.ConnectionId, projectId);

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"project-{projectId}");
        await _projectManager.UnsubscribeProjectAsync(projectId, Context.ConnectionId);
    }

    public async Task<string> GetMetricsHtml(string projectId)
    {
        _logger.LogInformation("Client {ConnectionId} requested metrics for project {ProjectId}",
            Context.ConnectionId, projectId);
        return await _projectManager.GetMetricsHtmlAsync(projectId);
    }

    public async Task DeleteProject(string projectId)
    {
        _logger.LogInformation("Client {ConnectionId} deleting project {ProjectId}",
            Context.ConnectionId, projectId);
        await _projectManager.DeleteProjectAsync(projectId);
        await Clients.All.ProjectDeleted(projectId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client {ConnectionId} disconnected", Context.ConnectionId);
        await _projectManager.CleanupConnectionAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
