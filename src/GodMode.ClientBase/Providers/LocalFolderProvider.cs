using GodMode.ClientBase.Abstractions;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GodMode.ClientBase.Providers;

/// <summary>
/// Server provider for local GodMode.Server instances.
/// </summary>
public class LocalFolderProvider : IServerProvider
{
    private readonly string _serverUrl;
    private readonly string _serverId;
    private readonly string _serverName;
    private readonly ILogger _logger;

    public string Type => "local";

    public LocalFolderProvider(string serverUrl = "http://localhost:31337", string? serverName = null,
        ILoggerFactory? loggerFactory = null)
    {
        _serverUrl = serverUrl;
        _serverId = "local-server";
        _serverName = serverName ?? "Local Server";
        _logger = (loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)
                  .CreateLogger<LocalFolderProvider>();
    }

    public async Task<IEnumerable<ServerInfo>> ListServersAsync()
    {
        var reachable = await IsServerReachableAsync();
        var state = reachable ? ServerState.Running : ServerState.Stopped;
        _logger.LogDebug("Local server {Url} reachable={Reachable} state={State}", _serverUrl, reachable, state);
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

        _logger.LogInformation("Connecting to local server at {Url}", _serverUrl);
        return await HubConnectionFactory.CreateAndStartAsync(_serverUrl);
    }

    private async Task<bool> IsServerReachableAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await client.GetAsync($"{_serverUrl}/health");
            _logger.LogDebug("Health check {Url}/health -> {StatusCode}", _serverUrl, response.StatusCode);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Health check {Url}/health failed: {Error}", _serverUrl, ex.Message);
            return false;
        }
    }
}
