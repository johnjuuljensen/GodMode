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

        try
        {
            var request = new CreateProjectRequest(profileName, projectRootName, inputs, actionName);
            var status = await _projectManager.CreateProjectAsync(request);
            await Clients.All.ProjectCreated(status);
            return status;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create project in root '{Root}'", projectRootName);
            throw new HubException(ex.Message);
        }
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

    public async Task SubscribeProject(string projectId, long fromOffset)
    {
        _logger.LogInformation("Client {ConnectionId} subscribing to project {ProjectId} from offset {Offset}",
            Context.ConnectionId, projectId, fromOffset);

        // Replays, then joins the project's group, in the order that loses and repeats nothing
        await _projectManager.SubscribeProjectAsync(projectId, fromOffset, Context.ConnectionId);
    }

    public async Task UnsubscribeProject(string projectId)
    {
        _logger.LogInformation("Client {ConnectionId} unsubscribing from project {ProjectId}",
            Context.ConnectionId, projectId);

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, ProjectLifecycle.OutputGroup(projectId));
        await _projectManager.UnsubscribeProjectAsync(projectId, Context.ConnectionId);
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

    public async Task ArchiveProject(string projectId)
    {
        _logger.LogInformation("Client {ConnectionId} archiving project {ProjectId}",
            Context.ConnectionId, projectId);
        try
        {
            await _projectManager.ArchiveProjectAsync(projectId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to archive project {ProjectId}", projectId);
            throw new HubException(ex.Message);
        }
        await Clients.All.ProjectArchived(projectId);
    }

    public async Task UnarchiveProject(string projectId)
    {
        _logger.LogInformation("Client {ConnectionId} unarchiving project {ProjectId}",
            Context.ConnectionId, projectId);
        try
        {
            var summary = await _projectManager.UnarchiveProjectAsync(projectId);
            await Clients.All.ProjectRestored(summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unarchive project {ProjectId}", projectId);
            throw new HubException(ex.Message);
        }
    }

    public async Task<ProjectSummary[]> ListArchivedProjects()
    {
        return await _projectManager.ListArchivedProjectsAsync();
    }

    public async Task CreateProfile(string name, string? description)
    {
        _logger.LogInformation("Client {ConnectionId} creating profile '{ProfileName}'",
            Context.ConnectionId, name);
        await _projectManager.CreateProfileAsync(name, description);
        await Clients.All.ProfilesChanged();
    }

    public async Task DeleteProfile(string name, bool deleteContents = false)
    {
        _logger.LogInformation("Client {ConnectionId} deleting profile '{ProfileName}' (deleteContents={DeleteContents})",
            Context.ConnectionId, name, deleteContents);
        try
        {
            await _projectManager.DeleteProfileAsync(name, deleteContents);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete profile '{ProfileName}'", name);
            throw new HubException(ex.Message);
        }
        await Clients.All.ProfilesChanged();
    }

    public async Task UpdateProfileDescription(string name, string? description)
    {
        _logger.LogInformation("Client {ConnectionId} updating profile description '{ProfileName}'",
            Context.ConnectionId, name);
        await _projectManager.UpdateProfileDescriptionAsync(name, description);
        await Clients.All.ProfilesChanged();
    }

    public async Task<string?> CheckCommand(string command)
    {
        // Only allow checking simple command names (no paths, no args)
        if (string.IsNullOrWhiteSpace(command) || command.Contains('/') || command.Contains('\\') || command.Contains(' '))
            return null;

        try
        {
            var which = OperatingSystem.IsWindows() ? "where" : "which";
            var psi = new System.Diagnostics.ProcessStartInfo(which, command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0 ? output.Trim().Split('\n')[0].Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client {ConnectionId} disconnected", Context.ConnectionId);
        await _projectManager.CleanupConnectionAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
