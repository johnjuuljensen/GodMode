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
    private readonly IConvergenceEngine _convergenceEngine;
    private readonly IManifestParser _manifestParser;
    private readonly ILogger<ProjectHub> _logger;

    public ProjectHub(IProjectManager projectManager, IConvergenceEngine convergenceEngine,
        IManifestParser manifestParser, ILogger<ProjectHub> logger)
    {
        _projectManager = projectManager;
        _convergenceEngine = convergenceEngine;
        _manifestParser = manifestParser;
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

    public async Task CreateRoot(string rootName, RootPreview preview, string? profileName = null)
    {
        _logger.LogInformation("Client {ConnectionId} creating root '{RootName}'",
            Context.ConnectionId, rootName);
        await _projectManager.CreateRootAsync(rootName, preview, profileName);
    }

    public async Task DeleteRoot(string profileName, string rootName, bool force = false)
    {
        _logger.LogInformation("Client {ConnectionId} deleting root '{RootName}' (force={Force})",
            Context.ConnectionId, rootName, force);
        await _projectManager.DeleteRootAsync(profileName, rootName, force);
    }

    public async Task<RootPreview?> GetRootPreview(string profileName, string rootName)
    {
        _logger.LogInformation("Client {ConnectionId} getting root preview for '{RootName}'",
            Context.ConnectionId, rootName);
        return await _projectManager.GetRootPreviewAsync(profileName, rootName);
    }

    public async Task UpdateRoot(string profileName, string rootName, RootPreview preview)
    {
        _logger.LogInformation("Client {ConnectionId} updating root '{RootName}'",
            Context.ConnectionId, rootName);
        await _projectManager.UpdateRootAsync(profileName, rootName, preview);
    }

    public async Task CreateProfile(string name, string? description)
    {
        _logger.LogInformation("Client {ConnectionId} creating profile '{ProfileName}'",
            Context.ConnectionId, name);
        await _projectManager.CreateProfileAsync(name, description);
    }

    public async Task UpdateProfileDescription(string name, string? description)
    {
        _logger.LogInformation("Client {ConnectionId} updating profile description '{ProfileName}'",
            Context.ConnectionId, name);
        await _projectManager.UpdateProfileDescriptionAsync(name, description);
    }

    public async Task<byte[]> ExportRoot(string profileName, string rootName)
    {
        _logger.LogInformation("Client {ConnectionId} exporting root '{RootName}'", Context.ConnectionId, rootName);
        return await _projectManager.ExportRootAsync(profileName, rootName);
    }

    public async Task<SharedRootPreview> PreviewImportFromBytes(byte[] packageBytes)
    {
        _logger.LogInformation("Client {ConnectionId} previewing import from bytes", Context.ConnectionId);
        return await _projectManager.PreviewImportFromBytesAsync(packageBytes);
    }

    public async Task<SharedRootPreview> PreviewImportFromUrl(string url)
    {
        _logger.LogInformation("Client {ConnectionId} previewing import from URL", Context.ConnectionId);
        return await _projectManager.PreviewImportFromUrlAsync(url);
    }

    public async Task<SharedRootPreview> PreviewImportFromGit(string gitUrl, string? path = null, string? gitRef = null)
    {
        _logger.LogInformation("Client {ConnectionId} previewing import from git {Url}", Context.ConnectionId, gitUrl);
        return await _projectManager.PreviewImportFromGitAsync(gitUrl, path, gitRef);
    }

    public async Task InstallSharedRoot(string rootName, SharedRootPreview preview)
    {
        _logger.LogInformation("Client {ConnectionId} installing shared root '{RootName}'", Context.ConnectionId, rootName);
        await _projectManager.InstallSharedRootAsync(rootName, preview);
    }

    public async Task UninstallSharedRoot(string rootName)
    {
        _logger.LogInformation("Client {ConnectionId} uninstalling shared root '{RootName}'", Context.ConnectionId, rootName);
        await _projectManager.UninstallSharedRootAsync(rootName);
    }

    public async Task<ConvergenceResult> ApplyManifest(string manifestContent, bool force = false)
    {
        _logger.LogInformation("Client {ConnectionId} applying manifest", Context.ConnectionId);
        var manifest = _manifestParser.Parse(manifestContent);
        return await _convergenceEngine.ConvergeAsync(manifest, force);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client {ConnectionId} disconnected", Context.ConnectionId);
        await _projectManager.CleanupConnectionAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
