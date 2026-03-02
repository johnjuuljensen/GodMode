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

    public async Task<ProfileInfo[]> ListProfiles()
    {
        _logger.LogInformation("Client {ConnectionId} requested profiles", Context.ConnectionId);
        return await _projectManager.ListProfilesAsync();
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

    public async Task<ProjectStatus> CreateProject(string profileName, string projectRootName, string? actionName, Dictionary<string, JsonElement> inputs)
    {
        _logger.LogInformation("Client {ConnectionId} creating project in profile '{Profile}' root '{Root}' action '{Action}' with {InputCount} inputs",
            Context.ConnectionId, profileName, projectRootName, actionName ?? "(default)", inputs.Count);

        var request = new CreateProjectRequest(profileName, projectRootName, inputs, actionName);
        var status = await _projectManager.CreateProjectAsync(request);

        // Notify all clients about the new project
        await Clients.All.ProjectCreated(status);

        return status;
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

    public async Task DeleteProject(string projectId, bool force = false)
    {
        _logger.LogInformation("Client {ConnectionId} deleting project {ProjectId} (force={Force})",
            Context.ConnectionId, projectId, force);

        try
        {
            await _projectManager.DeleteProjectAsync(projectId, force);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete project {ProjectId}", projectId);
            // Re-throw as HubException so SignalR propagates the message to the client
            // (regular exceptions are replaced with a generic message for security)
            throw new HubException(ex.Message);
        }

        await Clients.All.ProjectDeleted(projectId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client {ConnectionId} disconnected", Context.ConnectionId);
        await _projectManager.CleanupConnectionAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
