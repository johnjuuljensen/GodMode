using System.Text.Json;
using GodMode.AI;
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
    private readonly InferenceRouter _inference;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly ILogger<ProjectHub> _logger;

    public ProjectHub(IProjectManager projectManager, InferenceRouter inference, IHostApplicationLifetime appLifetime, ILogger<ProjectHub> logger)
    {
        _projectManager = projectManager;
        _inference = inference;
        _appLifetime = appLifetime;
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

    public async Task<McpRegistrySearchResult> SearchMcpServers(string query, int pageSize, int page)
    {
        _logger.LogInformation("Client {ConnectionId} searching MCP servers: '{Query}'",
            Context.ConnectionId, query);
        return await _projectManager.SearchMcpServersAsync(query, pageSize, page);
    }

    public async Task<McpServerDetail?> GetMcpServerDetail(string qualifiedName)
    {
        _logger.LogInformation("Client {ConnectionId} getting MCP server detail: '{Name}'",
            Context.ConnectionId, qualifiedName);
        return await _projectManager.GetMcpServerDetailAsync(qualifiedName);
    }

    public async Task AddMcpServer(string serverName, McpServerConfig config, string targetLevel,
        string? profileName, string? rootName, string? actionName)
    {
        _logger.LogInformation("Client {ConnectionId} adding MCP server '{Server}' to {Level}",
            Context.ConnectionId, serverName, targetLevel);
        await _projectManager.AddMcpServerAsync(serverName, config, targetLevel,
            profileName, rootName, actionName);
    }

    public async Task RemoveMcpServer(string serverName, string targetLevel,
        string? profileName, string? rootName, string? actionName)
    {
        _logger.LogInformation("Client {ConnectionId} removing MCP server '{Server}' from {Level}",
            Context.ConnectionId, serverName, targetLevel);
        await _projectManager.RemoveMcpServerAsync(serverName, targetLevel,
            profileName, rootName, actionName);
    }

    public async Task<Dictionary<string, McpServerConfig>> GetEffectiveMcpServers(
        string profileName, string rootName, string? actionName)
    {
        _logger.LogInformation("Client {ConnectionId} getting effective MCP servers for {Profile}/{Root}/{Action}",
            Context.ConnectionId, profileName, rootName, actionName ?? "(all)");
        return await _projectManager.GetEffectiveMcpServersAsync(profileName, rootName, actionName);
    }

    public async Task CreateProfile(string profileName, string? description)
    {
        _logger.LogInformation("Client {ConnectionId} creating profile '{Profile}'",
            Context.ConnectionId, profileName);
        await _projectManager.CreateProfileAsync(profileName, description);
    }

    public async Task UpdateProfileDescription(string profileName, string? description)
    {
        _logger.LogInformation("Client {ConnectionId} updating profile '{Profile}' description",
            Context.ConnectionId, profileName);
        await _projectManager.UpdateProfileDescriptionAsync(profileName, description);
    }

    // Root Creation & Management

    public async Task<RootTemplate[]> ListRootTemplates()
    {
        _logger.LogInformation("Client {ConnectionId} requested root templates", Context.ConnectionId);
        return await _projectManager.ListRootTemplatesAsync();
    }

    public async Task<RootPreview> PreviewRootFromTemplate(string templateName, Dictionary<string, string> parameters)
    {
        _logger.LogInformation("Client {ConnectionId} previewing template '{Template}'",
            Context.ConnectionId, templateName);
        return await _projectManager.PreviewRootFromTemplateAsync(templateName, parameters);
    }

    public async Task<RootPreview> GenerateRootWithLlm(RootGenerationRequest request)
    {
        _logger.LogInformation("Client {ConnectionId} generating root with LLM", Context.ConnectionId);
        return await _projectManager.GenerateRootWithLlmAsync(request);
    }

    public async Task CreateRoot(string profileName, string rootName, RootPreview preview)
    {
        _logger.LogInformation("Client {ConnectionId} creating root '{Root}' in profile '{Profile}'",
            Context.ConnectionId, rootName, profileName);
        await _projectManager.CreateRootAsync(profileName, rootName, preview);
    }

    public async Task<RootPreview> GetRootPreview(string profileName, string rootName)
    {
        _logger.LogInformation("Client {ConnectionId} getting root preview for '{Root}'",
            Context.ConnectionId, rootName);
        return await _projectManager.GetRootPreviewAsync(profileName, rootName);
    }

    public async Task UpdateRoot(string profileName, string rootName, RootPreview preview)
    {
        _logger.LogInformation("Client {ConnectionId} updating root '{Root}'",
            Context.ConnectionId, rootName);
        await _projectManager.UpdateRootAsync(profileName, rootName, preview);
    }

    // Root Sharing

    public async Task<byte[]> ExportRoot(string profileName, string rootName)
    {
        _logger.LogInformation("Client {ConnectionId} exporting root '{Root}'",
            Context.ConnectionId, rootName);
        try
        {
            return await _projectManager.ExportRootAsync(profileName, rootName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            throw new HubException(ex.Message);
        }
    }

    public async Task<SharedRootPreview> PreviewImportFromBytes(byte[] packageBytes)
    {
        _logger.LogInformation("Client {ConnectionId} previewing root import from file", Context.ConnectionId);
        return await _projectManager.PreviewImportFromBytesAsync(packageBytes);
    }

    public async Task<SharedRootPreview> PreviewImportFromUrl(string url)
    {
        _logger.LogInformation("Client {ConnectionId} previewing root import from URL: {Url}",
            Context.ConnectionId, url);
        return await _projectManager.PreviewImportFromUrlAsync(url);
    }

    public async Task<SharedRootPreview> PreviewImportFromGit(string repoUrl, string? subPath, string? gitRef)
    {
        _logger.LogInformation("Client {ConnectionId} previewing root import from git: {Url}",
            Context.ConnectionId, repoUrl);
        return await _projectManager.PreviewImportFromGitAsync(repoUrl, subPath, gitRef);
    }

    public async Task InstallSharedRoot(SharedRootPreview preview, string? localName)
    {
        _logger.LogInformation("Client {ConnectionId} installing shared root '{Name}'",
            Context.ConnectionId, preview.Manifest.Name);
        await _projectManager.InstallSharedRootAsync(preview, localName);
    }

    public async Task UninstallSharedRoot(string rootName)
    {
        _logger.LogInformation("Client {ConnectionId} uninstalling root '{Name}'",
            Context.ConnectionId, rootName);
        await _projectManager.UninstallSharedRootAsync(rootName);
    }

    public async Task<InferenceStatus> GetInferenceStatus()
    {
        if (!_inference.IsLoaded)
            await _inference.InitializeAsync();

        if (_inference.IsLoaded)
        {
            return new InferenceStatus(
                IsConfigured: true,
                Provider: _inference.LastUsedProvider ?? _inference.TierProviderMap.Values.FirstOrDefault(),
                Model: AIConfig.Load().Model);
        }

        return new InferenceStatus(IsConfigured: false);
    }

    public async Task<InferenceStatus> ConfigureInferenceApiKey(string apiKey)
    {
        _logger.LogInformation("Client {ConnectionId} configuring inference API key", Context.ConnectionId);

        var config = AIConfig.Load();
        config.ApiKey = apiKey.Trim();
        config.Provider ??= "anthropic";
        config.Model ??= "claude-sonnet-4-20250514";
        config.Save();

        // Reinitialize with the new key
        await _inference.InitializeAsync();

        if (_inference.IsLoaded)
        {
            return new InferenceStatus(
                IsConfigured: true,
                Provider: _inference.TierProviderMap.Values.FirstOrDefault(),
                Model: config.Model);
        }

        return new InferenceStatus(IsConfigured: false, Error: "API key saved but provider failed to load. Check the key is valid.");
    }

    public Task RestartServer()
    {
        _logger.LogInformation("Client {ConnectionId} requested server restart", Context.ConnectionId);

        // Spawn a new server process, then stop the current one
        _ = Task.Run(async () =>
        {
            await Task.Delay(500); // Give time for the response to reach the client

            var exe = Environment.ProcessPath;
            if (exe is not null)
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = Environment.CurrentDirectory,
                    UseShellExecute = false,
                };
                // Forward original command-line args
                foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
                    startInfo.ArgumentList.Add(arg);

                _logger.LogInformation("Spawning new server process: {Exe}", exe);
                System.Diagnostics.Process.Start(startInfo);
            }

            _appLifetime.StopApplication();
        });

        return Task.CompletedTask;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client {ConnectionId} disconnected", Context.ConnectionId);
        await _projectManager.CleanupConnectionAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
