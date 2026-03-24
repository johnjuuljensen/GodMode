using System.Text.Json;
using GodMode.Maui.Services;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR;

namespace GodMode.Maui.Hubs;

/// <summary>
/// Local SignalR hub that transparently proxies IProjectHub to the remote
/// GodMode.Server identified by the ?serverId query parameter.
/// Same contract as the remote hub — React doesn't know it's proxied.
/// </summary>
public class GodModeLocalHub : Hub<IProjectHubClient>, IProjectHub
{
    private readonly ServerConnectionManager _connections;

    public GodModeLocalHub(ServerConnectionManager connections)
    {
        _connections = connections;
    }

    public override async Task OnConnectedAsync()
    {
        var serverId = GetServerId();
        await Groups.AddToGroupAsync(Context.ConnectionId, $"server-{serverId}");
        await base.OnConnectedAsync();
    }

    // IProjectHub — transparent proxy to remote server

    public Task<ProfileInfo[]> ListProfiles() => Proxy().ListProfiles();
    public Task<ProjectRootInfo[]> ListProjectRoots() => Proxy().ListProjectRoots();
    public Task<ProjectSummary[]> ListProjects() => Proxy().ListProjects();
    public Task<ProjectStatus> GetStatus(string projectId) => Proxy().GetStatus(projectId);

    public Task<ProjectStatus> CreateProject(string profileName, string projectRootName,
        string? actionName, Dictionary<string, JsonElement> inputs) =>
        Proxy().CreateProject(profileName, projectRootName, actionName, inputs);

    public Task SendInput(string projectId, string input) => Proxy().SendInput(projectId, input);
    public Task StopProject(string projectId) => Proxy().StopProject(projectId);
    public Task ResumeProject(string projectId) => Proxy().ResumeProject(projectId);
    public Task SubscribeProject(string projectId, long outputOffset) => Proxy().SubscribeProject(projectId, outputOffset);
    public Task UnsubscribeProject(string projectId) => Proxy().UnsubscribeProject(projectId);
    public Task<string> GetMetricsHtml(string projectId) => Proxy().GetMetricsHtml(projectId);
    public Task DeleteProject(string projectId, bool force = false) => Proxy().DeleteProject(projectId, force);

    private string GetServerId() =>
        Context.GetHttpContext()?.Request.Query["serverId"].ToString()
        ?? throw new HubException("Missing serverId query parameter");

    private IProjectHub Proxy() =>
        _connections.GetHubProxy(GetServerId())
        ?? throw new HubException($"Server '{GetServerId()}' is not connected");
}
