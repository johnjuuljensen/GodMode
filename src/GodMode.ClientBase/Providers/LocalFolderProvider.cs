using GodMode.ClientBase.Abstractions;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.ClientBase.Providers;

/// <summary>
/// Server provider for local GodMode.Server instances.
/// </summary>
public class LocalFolderProvider : IServerProvider
{
    private readonly string _serverUrl;
    private readonly string _serverId;
    private readonly string _serverName;

    public string Type => "local";

    public LocalFolderProvider(string serverUrl = "http://localhost:31337", string? serverName = null)
    {
        _serverUrl = serverUrl;
        _serverId = "local-server";
        _serverName = serverName ?? "Local Server";
    }

    public async Task<IEnumerable<ServerInfo>> ListServersAsync()
    {
        var state = await IsServerReachableAsync() ? ServerState.Running : ServerState.Stopped;
        return [new ServerInfo(_serverId, _serverName, "local", state, _serverUrl)];
    }

    public async Task<ServerStatus> GetServerStatusAsync(string serverId)
    {
        var state = await IsServerReachableAsync() ? ServerState.Running : ServerState.Stopped;
        return new ServerStatus(_serverId, _serverName, "local", state, _serverUrl, 0, DateTime.UtcNow);
    }

    public Task StartServerAsync(string serverId) => Task.CompletedTask;
    public Task StopServerAsync(string serverId) => Task.CompletedTask;

    public async Task<HubConnection> ConnectAsync(string serverId)
    {
        if (serverId != _serverId)
            throw new ArgumentException($"Unknown server: {serverId}");

        return await HubConnectionFactory.CreateAndStartAsync(_serverUrl);
    }

    private async Task<bool> IsServerReachableAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await client.GetAsync($"{_serverUrl}/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
