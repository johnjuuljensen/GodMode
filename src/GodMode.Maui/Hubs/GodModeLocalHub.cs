using GodMode.Shared.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace GodMode.Maui.Hubs;

/// <summary>
/// Local SignalR hub that transparently proxies to the remote GodMode.Server
/// identified by the ?serverId query parameter. The actual proxying is done
/// by <see cref="ProxyHubFilter"/> — this hub is an empty shell.
/// </summary>
public class GodModeLocalHub : Hub<IProjectHubClient>
{
    private readonly Services.ServerConnectionManager _connections;

    public GodModeLocalHub(Services.ServerConnectionManager connections)
    {
        _connections = connections;
    }

    public override async Task OnConnectedAsync()
    {
        var serverId = Context.GetHttpContext()?.Request.Query["serverId"].ToString();
        if (!string.IsNullOrEmpty(serverId))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"server-{serverId}");
        await base.OnConnectedAsync();
    }
}
