using GodMode.Shared.Models;
using GodMode.Server.Services;
using Microsoft.AspNetCore.SignalR;

namespace GodMode.Server.Hubs;

/// <summary>
/// SignalR hub for real-time project communication.
/// </summary>
public class ProjectHub : Hub
{
    private readonly IProjectManager _projectManager;
    private readonly ILogger<ProjectHub> _logger;

    public ProjectHub(IProjectManager projectManager, ILogger<ProjectHub> logger)
    {
        _projectManager = projectManager;
        _logger = logger;
    }

    /// <summary>
    /// Lists all projects.
    /// </summary>
    public async Task<ProjectSummary[]> ListProjects()
    {
        _logger.LogInformation("Client {ConnectionId} requested project list", Context.ConnectionId);
        return await _projectManager.ListProjectsAsync();
    }

    /// <summary>
    /// Gets the status of a specific project.
    /// </summary>
    public async Task<ProjectStatus> GetStatus(string projectId)
    {
        _logger.LogInformation("Client {ConnectionId} requested status for project {ProjectId}", 
            Context.ConnectionId, projectId);
        return await _projectManager.GetStatusAsync(projectId);
    }

    /// <summary>
    /// Creates a new project.
    /// </summary>
    public async Task<ProjectDetail> CreateProject(string name, string? repoUrl, string initialPrompt)
    {
        _logger.LogInformation("Client {ConnectionId} creating project {Name}", Context.ConnectionId, name);
        
        var request = new CreateProjectRequest(name, repoUrl, initialPrompt);
        var project = await _projectManager.CreateProjectAsync(request);
        
        // Notify all clients about the new project
        await Clients.All.SendAsync("ProjectCreated", project.Status);
        
        return project;
    }

    /// <summary>
    /// Sends input to a project.
    /// </summary>
    public async Task SendInput(string projectId, string input)
    {
        _logger.LogInformation("Client {ConnectionId} sending input to project {ProjectId}", 
            Context.ConnectionId, projectId);
        await _projectManager.SendInputAsync(projectId, input);
    }

    /// <summary>
    /// Stops a running project.
    /// </summary>
    public async Task StopProject(string projectId)
    {
        _logger.LogInformation("Client {ConnectionId} stopping project {ProjectId}", 
            Context.ConnectionId, projectId);
        await _projectManager.StopProjectAsync(projectId);
    }

    /// <summary>
    /// Subscribes to output events from a project.
    /// </summary>
    public async Task SubscribeProject(string projectId, long outputOffset)
    {
        _logger.LogInformation("Client {ConnectionId} subscribing to project {ProjectId} from offset {Offset}", 
            Context.ConnectionId, projectId, outputOffset);
        
        await Groups.AddToGroupAsync(Context.ConnectionId, $"project-{projectId}");
        await _projectManager.SubscribeProjectAsync(projectId, outputOffset, Context.ConnectionId);
    }

    /// <summary>
    /// Unsubscribes from output events from a project.
    /// </summary>
    public async Task UnsubscribeProject(string projectId)
    {
        _logger.LogInformation("Client {ConnectionId} unsubscribing from project {ProjectId}", 
            Context.ConnectionId, projectId);
        
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"project-{projectId}");
        await _projectManager.UnsubscribeProjectAsync(projectId, Context.ConnectionId);
    }

    /// <summary>
    /// Gets the metrics HTML for a project.
    /// </summary>
    public async Task<string> GetMetricsHtml(string projectId)
    {
        _logger.LogInformation("Client {ConnectionId} requested metrics for project {ProjectId}", 
            Context.ConnectionId, projectId);
        return await _projectManager.GetMetricsHtmlAsync(projectId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client {ConnectionId} disconnected", Context.ConnectionId);
        await _projectManager.CleanupConnectionAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
