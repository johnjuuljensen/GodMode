using System.Text.Json;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using GodMode.Server.Models;
using GodMode.Server.Services;
using Microsoft.AspNetCore.SignalR;

namespace GodMode.Server.Hubs;

/// <summary>
/// SignalR hub for real-time project communication.
/// </summary>
public class ProjectHub : Hub<IProjectHubClient>, IProjectHub
{
    /// <summary>How many of one connection's calls run at once (SignalR's MaximumParallelInvocationsPerClient).</summary>
    public const int ParallelInvocationsPerClient = 4;

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

    public async Task<CreateProjectResult> CreateProject(string profileName, string projectRootName, string? actionName, Dictionary<string, JsonElement> inputs)
    {
        _logger.LogInformation("Client {ConnectionId} creating project in profile '{Profile}' root '{Root}' action '{Action}' with {InputCount} inputs",
            Context.ConnectionId, profileName, projectRootName, actionName ?? "(default)", inputs.Count);

        try
        {
            var request = new CreateProjectRequest(profileName, projectRootName, inputs, actionName, ParentOf(inputs));
            var result = await _projectManager.CreateProjectAsync(request);
            // An action that starts no session made no project to list
            if (result.Project is { } status) await Clients.All.ProjectCreated(status);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create project in root '{Root}'", projectRootName);
            throw new HubException(ex.Message);
        }
    }

    /// <summary>
    /// The parent a create names in its <see cref="CreateProjectRequest.ParentInput"/> input: a string, or
    /// none when it is missing, null or blank. Any other value is refused, rather than starting a top-level session.
    /// </summary>
    private static string? ParentOf(Dictionary<string, JsonElement> inputs) =>
        inputs.TryGetValue(CreateProjectRequest.ParentInput, out var parent)
            ? parent.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => string.IsNullOrWhiteSpace(parent.GetString()) ? null : parent.GetString(),
                _ => throw new ArgumentException($"The create's {CreateProjectRequest.ParentInput} is not a session ID: {parent.GetRawText()}."),
            }
            : null;

    public async Task SendInput(string projectId, string input)
    {
        _logger.LogInformation("Client {ConnectionId} sending input to project {ProjectId}",
            Context.ConnectionId, projectId);
        try
        {
            await _projectManager.SendInputAsync(projectId, input);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            throw new HubException(ex.Message);
        }
    }

    public Task<AttentionItem[]> GetAttention()
    {
        _logger.LogInformation("Client {ConnectionId} requested the attention list", Context.ConnectionId);
        return Task.FromResult(_projectManager.GetAttention());
    }

    public async Task MarkSeen(string projectId)
    {
        _logger.LogInformation("Client {ConnectionId} saw project {ProjectId}", Context.ConnectionId, projectId);
        try
        {
            await _projectManager.MarkSeenAsync(projectId);
        }
        catch (KeyNotFoundException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public async Task ReplyAndResume(string projectId, string text)
    {
        _logger.LogInformation("Client {ConnectionId} replying to project {ProjectId}", Context.ConnectionId, projectId);
        try
        {
            await _projectManager.ReplyAndResumeAsync(projectId, text);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or TimeoutException or LaunchConfigException)
        {
            throw new HubException(ex.Message);
        }
    }

    public async Task RespondToPermission(string projectId, string requestId, PermissionDecision decision)
    {
        _logger.LogInformation("Client {ConnectionId} answering permission request {RequestId} of project {ProjectId}",
            Context.ConnectionId, requestId, projectId);
        try
        {
            await _projectManager.RespondToPermissionAsync(projectId, requestId, decision);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            throw new HubException(ex.Message);
        }
    }

    public async Task<PermissionDetail> GetPermissionDetail(string projectId, string requestId)
    {
        try
        {
            return await _projectManager.GetPermissionDetailAsync(projectId, requestId);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            throw new HubException(ex.Message);
        }
    }

    public async Task AnswerQuestion(string projectId, string requestId, Dictionary<string, string> answers)
    {
        _logger.LogInformation("Client {ConnectionId} answering question {RequestId} of project {ProjectId}",
            Context.ConnectionId, requestId, projectId);
        try
        {
            await _projectManager.AnswerQuestionAsync(projectId, requestId, answers);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new HubException(ex.Message);
        }
    }

    public async Task StopProject(string projectId)
    {
        _logger.LogInformation("Client {ConnectionId} stopping project {ProjectId}",
            Context.ConnectionId, projectId);
        try
        {
            await _projectManager.StopProjectAsync(projectId);
        }
        catch (CreateFailedException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public async Task ResumeProject(string projectId)
    {
        _logger.LogInformation("Client {ConnectionId} resuming project {ProjectId}",
            Context.ConnectionId, projectId);
        try
        {
            await _projectManager.ResumeProjectAsync(projectId);
        }
        catch (CreateFailedException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public async Task SubscribeProject(string projectId, long fromOffset, string subscriptionId, string? generation)
    {
        _logger.LogInformation("Client {ConnectionId} subscribing ({SubscriptionId}) to project {ProjectId} from offset {Offset} of generation {Generation}",
            Context.ConnectionId, subscriptionId, projectId, fromOffset, generation ?? "(none)");

        // Replays, then joins the project's group, in the order that loses and repeats nothing
        await _projectManager.SubscribeProjectAsync(projectId, fromOffset, subscriptionId, generation, Context.ConnectionId);
    }

    public async Task<AssistantReply[]> GetLastReplies(string projectId, int turns)
    {
        _logger.LogInformation("Client {ConnectionId} reading the last {Turns} replies of project {ProjectId}",
            Context.ConnectionId, turns, projectId);
        if (turns is < 1 or > IProjectHub.MaxReplyTurns)
            throw new HubException($"turns must be 1 to {IProjectHub.MaxReplyTurns}, not {turns}.");
        try
        {
            return [.. await _projectManager.LastRepliesAsync(projectId, turns)];
        }
        catch (KeyNotFoundException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public async Task UnsubscribeProject(string projectId)
    {
        _logger.LogInformation("Client {ConnectionId} unsubscribing from project {ProjectId}",
            Context.ConnectionId, projectId);

        // Leaves the live group there, in turn with the connection's subscribes
        await _projectManager.UnsubscribeProjectAsync(projectId, Context.ConnectionId);
    }

    public async Task<DeleteProjectResult> DeleteProject(string projectId, bool force = false)
    {
        _logger.LogInformation("Client {ConnectionId} deleting project {ProjectId} (force={Force})",
            Context.ConnectionId, projectId, force);

        DeleteProjectResult result;
        try
        {
            result = await _projectManager.DeleteProjectAsync(projectId, force);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete project {ProjectId}", projectId);
            // Re-throw as HubException so SignalR propagates the message to the client
            // (regular exceptions are replaced with a generic message for security)
            throw new HubException(ex.Message);
        }

        await Clients.All.ProjectDeleted(projectId);
        return result;
    }

    public async Task<ProjectStatus> RestoreProject(string projectId)
    {
        _logger.LogInformation("Client {ConnectionId} restoring project {ProjectId} from the trash", Context.ConnectionId, projectId);
        try
        {
            // Pushed as ProjectCreated to every client, this one included
            return await _projectManager.RestoreProjectAsync(projectId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Project {ProjectId} was not restored: {Reason}", projectId, ex.Message);
            throw new HubException(ex.Message);
        }
    }

    public async Task<UnmanagedFolder[]> ListUnmanaged(string profileName, string projectRootName)
    {
        _logger.LogInformation("Client {ConnectionId} listing the folders of profile '{Profile}' root '{Root}' GodMode does not manage",
            Context.ConnectionId, profileName, projectRootName);
        try
        {
            return await _projectManager.ListUnmanagedAsync(profileName, projectRootName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Listing root '{Root}''s unmanaged folders failed: {Reason}", projectRootName, ex.Message);
            throw new HubException(ex.Message);
        }
    }

    public async Task<ProjectStatus> AdoptFolder(string profileName, string projectRootName, string path, string? actionName, Dictionary<string, JsonElement>? inputs)
    {
        _logger.LogInformation("Client {ConnectionId} adopting '{Path}' in profile '{Profile}' root '{Root}' with action '{Action}'",
            Context.ConnectionId, path, profileName, projectRootName, actionName ?? "(default)");
        ProjectStatus status;
        try
        {
            status = await _projectManager.AdoptFolderAsync(profileName, projectRootName, path, actionName, inputs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to adopt '{Path}' in root '{Root}'", path, projectRootName);
            throw new HubException(ex.Message);
        }
        await Clients.All.ProjectCreated(status);
        return status;
    }

    public async Task<DeleteProjectResult> ForgetProject(string projectId)
    {
        _logger.LogInformation("Client {ConnectionId} forgetting project {ProjectId}", Context.ConnectionId, projectId);
        DeleteProjectResult result;
        try
        {
            result = await _projectManager.ForgetProjectAsync(projectId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to forget project {ProjectId}", projectId);
            throw new HubException(ex.Message);
        }

        await Clients.All.ProjectDeleted(projectId);
        return result;
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
