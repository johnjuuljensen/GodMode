using GodMode.ClientBase.Abstractions;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GodMode.ClientBase.Providers;

/// <summary>
/// Host provider for local GodMode.Server instances.
/// </summary>
public class LocalFolderProvider : IServerProvider
{
    private readonly string _serverUrl;
    private readonly string _hostId;
    private readonly string _hostName;
    private readonly ILogger _logger;

    public string Type => "local";

    public LocalFolderProvider(string serverUrl = "http://localhost:31337", string? hostName = null,
        ILoggerFactory? loggerFactory = null)
    {
        _serverUrl = serverUrl;
        _hostId = "local-server";
        _hostName = hostName ?? "Local Server";
        _logger = (loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)
                  .CreateLogger<LocalFolderProvider>();
    }

    public async Task<IEnumerable<ServerInfo>> ListHostsAsync()
    {
        var reachable = await IsServerReachableAsync();
        var state = reachable ? ServerState.Running : ServerState.Stopped;
        _logger.LogDebug("Local server {Url} reachable={Reachable} state={State}", _serverUrl, reachable, state);
        return [new ServerInfo(_hostId, _hostName, "local", state, _serverUrl)];
    }

    public async Task<ServerStatus> GetServerStatusAsync(string hostId)
    {
        var state = await IsServerReachableAsync() ? ServerState.Running : ServerState.Stopped;
        return new ServerStatus(_hostId, _hostName, "local", state, _serverUrl, 0, DateTime.UtcNow);
    }

    public Task StartHostAsync(string hostId) => Task.CompletedTask;
    public Task StopHostAsync(string hostId) => Task.CompletedTask;

    public async Task<HubConnection> ConnectAsync(string hostId)
    {
        if (hostId != _hostId)
            throw new ArgumentException($"Unknown host: {hostId}");

        _logger.LogInformation("Connecting to local server at {Url}", _serverUrl);

        var hubUrl = _serverUrl.TrimEnd('/') + "/hubs/projects";
        var builder = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .AddJsonProtocol(options =>
            {
                var defaults = GodMode.Shared.JsonDefaults.Options;
                options.PayloadSerializerOptions.PropertyNamingPolicy = defaults.PropertyNamingPolicy;
                options.PayloadSerializerOptions.DefaultIgnoreCondition = defaults.DefaultIgnoreCondition;
                foreach (var converter in defaults.Converters)
                    options.PayloadSerializerOptions.Converters.Add(converter);
            });

        var connection = builder.Build();
        await connection.StartAsync();
        return connection;
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
