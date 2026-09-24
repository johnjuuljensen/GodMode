using GodMode.ClientBase.Abstractions;
using GodMode.ClientBase.Services;
using GodMode.ClientBase.Services.Models;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using SignalR.Proxy;

namespace GodMode.ClientBase.Providers;

/// <summary>
/// Server provider for one registered GodMode.Server, reached at the first of its URLs that answers.
/// The server's ID is its registration's ID.
/// </summary>
public class LocalFolderProvider(ServerRegistration registration, string? accessToken, ServerUrlSelector urlSelector)
    : IServerProvider
{
    public string Type => ServerTypes.Local;

    private string Name => registration.DisplayName ?? registration.Urls.FirstOrDefault() ?? "Local Server";

    public async Task<IReadOnlyList<ServerInfo>> ListServersAsync(CancellationToken ct = default)
    {
        var url = await urlSelector.SelectAsync(registration.Urls, ct);
        var state = url != null ? ServerState.Running : ServerState.Stopped;
        return [new ServerInfo(registration.Id, Name, ServerTypes.Local, state, url ?? registration.Urls.FirstOrDefault())];
    }

    public Task<bool> OwnsAsync(string serverId) => Task.FromResult(serverId == registration.Id);

    public Task StartServerAsync(string serverId) => Task.CompletedTask;
    public Task StopServerAsync(string serverId) => Task.CompletedTask;

    public async Task<RelayTarget?> ResolveAsync(string serverId, CancellationToken ct = default)
    {
        if (serverId != registration.Id) return null;
        var url = await urlSelector.SelectAsync(registration.Urls, ct);
        return url == null ? null : new RelayTarget($"{url}/hubs/projects", accessToken);
    }
}
