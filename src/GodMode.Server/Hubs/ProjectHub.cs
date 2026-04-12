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
    private readonly IManifestExporter _manifestExporter;
    private readonly OAuthTokenStore _oauthTokenStore;
    private readonly ScheduleManager _scheduleManager;
    private readonly RootGenerationService? _rootGenerationService;
    private readonly GodModeChatService? _chatService;
    private readonly ILogger<ProjectHub> _logger;

    public ProjectHub(IProjectManager projectManager, IConvergenceEngine convergenceEngine,
        IManifestParser manifestParser, IManifestExporter manifestExporter,
        OAuthTokenStore oauthTokenStore, ScheduleManager scheduleManager, ILogger<ProjectHub> logger,
        RootGenerationService? rootGenerationService = null,
        GodModeChatService? chatService = null)
    {
        _projectManager = projectManager;
        _convergenceEngine = convergenceEngine;
        _manifestParser = manifestParser;
        _manifestExporter = manifestExporter;
        _oauthTokenStore = oauthTokenStore;
        _scheduleManager = scheduleManager;
        _rootGenerationService = rootGenerationService;
        _chatService = chatService;
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

    public async Task AddMcpServer(string serverName, McpServerConfig config, string targetLevel,
        string? profileName = null, string? rootName = null, string? actionName = null)
    {
        _logger.LogInformation("Client {ConnectionId} adding MCP server '{ServerName}' at {Level}",
            Context.ConnectionId, serverName, targetLevel);
        await _projectManager.AddMcpServerAsync(serverName, config, targetLevel, profileName, rootName, actionName);
    }

    public async Task RemoveMcpServer(string serverName, string targetLevel,
        string? profileName = null, string? rootName = null, string? actionName = null)
    {
        _logger.LogInformation("Client {ConnectionId} removing MCP server '{ServerName}' at {Level}",
            Context.ConnectionId, serverName, targetLevel);
        await _projectManager.RemoveMcpServerAsync(serverName, targetLevel, profileName, rootName, actionName);
    }

    public async Task<Dictionary<string, McpServerConfig>> GetEffectiveMcpServers(
        string profileName, string rootName, string? actionName = null)
    {
        _logger.LogInformation("Client {ConnectionId} requesting effective MCP servers for {Profile}/{Root}/{Action}",
            Context.ConnectionId, profileName, rootName, actionName ?? "(all)");
        return await _projectManager.GetEffectiveMcpServersAsync(profileName, rootName, actionName);
    }

    public async Task CreateRoot(string rootName, RootPreview preview, string? profileName = null)
    {
        _logger.LogInformation("Client {ConnectionId} creating root '{RootName}'",
            Context.ConnectionId, rootName);
        await _projectManager.CreateRootAsync(rootName, preview, profileName);
        await Clients.All.RootsChanged();
    }

    public async Task DeleteRoot(string profileName, string rootName, bool force = false)
    {
        _logger.LogInformation("Client {ConnectionId} deleting root '{RootName}' (force={Force})",
            Context.ConnectionId, rootName, force);
        try
        {
            await _projectManager.DeleteRootAsync(profileName, rootName, force);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete root '{RootName}'", rootName);
            throw new HubException(ex.Message);
        }
        await Clients.All.RootsChanged();
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
        await Clients.All.RootsChanged();
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

    public Task<string> ExportManifest()
    {
        _logger.LogInformation("Client {ConnectionId} exporting manifest", Context.ConnectionId);
        var manifest = _manifestExporter.Export();
        return Task.FromResult(_manifestExporter.Serialize(manifest));
    }

    public async Task<RootPreview> GenerateRootWithLlm(RootGenerationRequest request)
    {
        if (_rootGenerationService == null)
            throw new HubException("LLM root generation is not available. Configure inference in ~/.godmode/inference.json.");

        _logger.LogInformation("Client {ConnectionId} generating root with LLM: {Instruction}",
            Context.ConnectionId, request.Instruction);
        return await _rootGenerationService.GenerateAsync(request);
    }

    public async Task SendChatMessage(string message)
    {
        if (_chatService == null)
            throw new HubException("GodMode chat is not available. Configure inference in ~/.godmode/inference.json.");

        _logger.LogInformation("Client {ConnectionId} sending chat message", Context.ConnectionId);
        await _chatService.ProcessMessageAsync(
            Context.ConnectionId,
            message,
            async msg => await Clients.Caller.ChatResponse(msg));
    }

    public Task ClearChatHistory()
    {
        _chatService?.RemoveSession(Context.ConnectionId);
        return Task.CompletedTask;
    }

    // ── OAuth ──

    public Task<Dictionary<string, OAuthProviderStatus>> GetOAuthStatus(string profileName)
    {
        _logger.LogInformation("Client {ConnectionId} requested OAuth status for profile '{Profile}'",
            Context.ConnectionId, profileName);
        return Task.FromResult(_oauthTokenStore.GetProviderStatuses(profileName));
    }

    public async Task DisconnectOAuthProvider(string profileName, string provider)
    {
        _logger.LogInformation("Client {ConnectionId} disconnecting OAuth provider '{Provider}' from profile '{Profile}'",
            Context.ConnectionId, provider, profileName);
        _oauthTokenStore.DeleteTokens(profileName, provider);
        await Clients.All.OAuthStatusChanged(profileName);
    }

    // ── Webhooks ──

    public async Task<WebhookInfo[]> ListWebhooks()
    {
        _logger.LogInformation("Client {ConnectionId} requested webhooks", Context.ConnectionId);
        return await _projectManager.ListWebhooksAsync();
    }

    public async Task<WebhookInfo> CreateWebhook(string keyword, string profileName, string rootName,
        string? actionName = null, string? description = null,
        Dictionary<string, string>? inputMapping = null,
        Dictionary<string, JsonElement>? staticInputs = null)
    {
        _logger.LogInformation("Client {ConnectionId} creating webhook '{Keyword}'", Context.ConnectionId, keyword);
        var info = await _projectManager.CreateWebhookAsync(keyword, profileName, rootName, actionName, description, inputMapping, staticInputs);
        await Clients.All.WebhooksChanged();
        return info;
    }

    public async Task DeleteWebhook(string keyword)
    {
        _logger.LogInformation("Client {ConnectionId} deleting webhook '{Keyword}'", Context.ConnectionId, keyword);
        await _projectManager.DeleteWebhookAsync(keyword);
        await Clients.All.WebhooksChanged();
    }

    public async Task<WebhookInfo> UpdateWebhook(string keyword, string? description = null,
        Dictionary<string, string>? inputMapping = null,
        Dictionary<string, JsonElement>? staticInputs = null,
        bool? enabled = null)
    {
        _logger.LogInformation("Client {ConnectionId} updating webhook '{Keyword}'", Context.ConnectionId, keyword);
        var info = await _projectManager.UpdateWebhookAsync(keyword, description, inputMapping, staticInputs, enabled);
        await Clients.All.WebhooksChanged();
        return info;
    }

    public async Task<string> RegenerateWebhookToken(string keyword)
    {
        _logger.LogInformation("Client {ConnectionId} regenerating token for webhook '{Keyword}'", Context.ConnectionId, keyword);
        var token = await _projectManager.RegenerateWebhookTokenAsync(keyword);
        await Clients.All.WebhooksChanged();
        return token;
    }

    // ── Schedules ──

    public Task<ScheduleInfo[]> GetSchedules(string profileName)
    {
        _logger.LogInformation("Client {ConnectionId} listing schedules for {Profile}", Context.ConnectionId, profileName);
        return Task.FromResult(_scheduleManager.GetSchedules(profileName).ToArray());
    }

    public Task<ScheduleInfo> CreateSchedule(string profileName, string name, ScheduleConfig config)
    {
        _logger.LogInformation("Client {ConnectionId} creating schedule {Profile}/{Name}", Context.ConnectionId, profileName, name);
        return Task.FromResult(_scheduleManager.CreateSchedule(profileName, name, config));
    }

    public Task<ScheduleInfo> UpdateSchedule(string profileName, string name, ScheduleConfig config)
    {
        _logger.LogInformation("Client {ConnectionId} updating schedule {Profile}/{Name}", Context.ConnectionId, profileName, name);
        return Task.FromResult(_scheduleManager.UpdateSchedule(profileName, name, config));
    }

    public Task DeleteSchedule(string profileName, string name)
    {
        _logger.LogInformation("Client {ConnectionId} deleting schedule {Profile}/{Name}", Context.ConnectionId, profileName, name);
        _scheduleManager.DeleteSchedule(profileName, name);
        return Task.CompletedTask;
    }

    public Task<ScheduleInfo> ToggleSchedule(string profileName, string name, bool enabled)
    {
        _logger.LogInformation("Client {ConnectionId} toggling schedule {Profile}/{Name} → {Enabled}", Context.ConnectionId, profileName, name, enabled);
        return Task.FromResult(_scheduleManager.ToggleSchedule(profileName, name, enabled));
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
        _chatService?.RemoveSession(Context.ConnectionId);
        await _projectManager.CleanupConnectionAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
