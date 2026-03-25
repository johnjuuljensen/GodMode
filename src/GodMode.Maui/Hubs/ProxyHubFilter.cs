using GodMode.Maui.Services;
using Microsoft.AspNetCore.SignalR;

namespace GodMode.Maui.Hubs;

/// <summary>
/// SignalR hub filter that intercepts all method invocations on GodModeLocalHub
/// and forwards them to the remote GodMode.Server via the raw HubConnection.
/// No method stubs needed — when IProjectHub changes, this just works.
/// </summary>
public class ProxyHubFilter(ServerConnectionManager connections) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext context,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        // Only proxy GodModeLocalHub invocations
        if (context.Hub is not GodModeLocalHub)
            return await next(context);

        var serverId = context.Context.GetHttpContext()?.Request.Query["serverId"].ToString();
        if (string.IsNullOrEmpty(serverId))
            throw new HubException("Missing serverId query parameter");

        var remote = connections.GetRemoteConnection(serverId)
            ?? throw new HubException($"Server '{serverId}' is not connected");

        return await remote.InvokeCoreAsync(
            context.HubMethodName,
            typeof(object),
            context.HubMethodArguments.ToArray(),
            context.Context.ConnectionAborted);
    }
}
