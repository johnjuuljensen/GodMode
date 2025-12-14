using GodMode.Maui.Abstractions;
using GodMode.Shared.Models;
using GodMode.Shared.Enums;

namespace GodMode.Maui.Providers;

/// <summary>
/// Host provider for local server connections.
/// All project operations go through the GodMode.Server via SignalR.
/// </summary>
public class LocalFolderProvider : IHostProvider
{
    private readonly string _serverUrl;
    private readonly string _hostId;
    private readonly string _hostName;

    public string Type => "local";

    /// <summary>
    /// Creates a new LocalFolderProvider that connects to a local GodMode.Server instance.
    /// </summary>
    /// <param name="serverUrl">The server URL (e.g., "http://localhost:5000").</param>
    /// <param name="hostName">Optional display name for the host.</param>
    public LocalFolderProvider(string serverUrl = "http://localhost:5000", string? hostName = null)
    {
        _serverUrl = serverUrl;
        _hostId = "local-server";
        _hostName = hostName ?? "Local Server";
    }

    public async Task<IEnumerable<HostInfo>> ListHostsAsync()
    {
        // Check if server is reachable (async to avoid blocking UI)
        var state = await IsServerReachableAsync() ? HostState.Running : HostState.Stopped;

        var hosts = new List<HostInfo>
        {
            new HostInfo(
                _hostId,
                _hostName,
                "local",
                state,
                _serverUrl
            )
        };

        return hosts;
    }

    public async Task<HostStatus> GetHostStatusAsync(string hostId)
    {
        if (hostId != _hostId)
        {
            throw new ArgumentException($"Unknown host: {hostId}");
        }

        var state = await IsServerReachableAsync() ? HostState.Running : HostState.Stopped;

        var status = new HostStatus(
            _hostId,
            _hostName,
            "local",
            state,
            _serverUrl,
            0, // Active projects - would need to query server
            DateTime.UtcNow
        );

        return status;
    }

    public Task StartHostAsync(string hostId)
    {
        // Local server must be started externally (e.g., via command line)
        // This could potentially launch the server process in the future
        return Task.CompletedTask;
    }

    public Task StopHostAsync(string hostId)
    {
        // Local server can't be stopped from the UI
        return Task.CompletedTask;
    }

    public async Task<IProjectConnection> ConnectAsync(string hostId)
    {
        if (hostId != _hostId)
        {
            throw new ArgumentException($"Unknown host: {hostId}");
        }

        var hubUrl = $"{_serverUrl.TrimEnd('/')}/hubs/projects";
        var connection = new SignalRProjectConnection(hubUrl);

        try
        {
            await connection.ConnectAsync();
            return connection;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to connect to local server at {_serverUrl}. " +
                "Make sure GodMode.Server is running.",
                ex);
        }
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
