using GodMode.ClientBase.Abstractions;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.ClientBase.Providers;

/// <summary>
/// Host provider for local GodMode.Server instances.
/// </summary>
public class LocalFolderProvider : IHostProvider
{
    private readonly string _serverUrl;
    private readonly string _hostId;
    private readonly string _hostName;

    public string Type => "local";

    public LocalFolderProvider(string serverUrl = "http://localhost:31337", string? hostName = null)
    {
        _serverUrl = serverUrl;
        _hostId = "local-server";
        _hostName = hostName ?? "Local Server";
    }

    public async Task<IEnumerable<HostInfo>> ListHostsAsync()
    {
        var state = await IsServerReachableAsync() ? HostState.Running : HostState.Stopped;
        return [new HostInfo(_hostId, _hostName, "local", state, _serverUrl)];
    }

    public async Task<HostStatus> GetHostStatusAsync(string hostId)
    {
        var state = await IsServerReachableAsync() ? HostState.Running : HostState.Stopped;
        return new HostStatus(_hostId, _hostName, "local", state, _serverUrl, 0, DateTime.UtcNow);
    }

    public Task StartHostAsync(string hostId) => Task.CompletedTask;
    public Task StopHostAsync(string hostId) => Task.CompletedTask;

    public async Task<HubConnection> ConnectAsync(string hostId)
    {
        if (hostId != _hostId)
            throw new ArgumentException($"Unknown host: {hostId}");

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
