using GodMode.Maui.Abstractions;
using GodMode.Shared.Models;
using GodMode.Shared.Enums;
using Octokit;

namespace GodMode.Maui.Providers;

/// <summary>
/// Host provider for GitHub Codespaces
/// </summary>
public class GitHubCodespaceProvider : IHostProvider
{
    private readonly GitHubClient _client;
    private readonly string _username;

    public string Type => "github";

    public GitHubCodespaceProvider(string token, string username)
    {
        _username = username;
        _client = new GitHubClient(new ProductHeaderValue("GodMode"))
        {
            Credentials = new Credentials(token)
        };
    }

    public async Task<IEnumerable<HostInfo>> ListHostsAsync()
    {
        var hosts = new List<HostInfo>();

        try
        {
            // Note: Octokit doesn't have built-in Codespaces API support yet
            // We'll need to use the REST API directly
            var codespaces = await GetCodespacesViaRestApi();

            foreach (var codespace in codespaces)
            {
                hosts.Add(new HostInfo(
                    codespace.Name,
                    codespace.DisplayName ?? codespace.Name,
                    "github",
                    MapCodespaceState(codespace.State),
                    codespace.WebUrl
                ));
            }
        }
        catch (Exception ex)
        {
            // Log error and return empty list
            Console.WriteLine($"Error listing codespaces: {ex.Message}");
        }

        return hosts;
    }

    public async Task<HostStatus> GetHostStatusAsync(string hostId)
    {
        var codespace = await GetCodespaceByName(hostId);

        if (codespace == null)
        {
            throw new InvalidOperationException($"Codespace {hostId} not found");
        }

        return new HostStatus(
            codespace.Name,
            codespace.DisplayName ?? codespace.Name,
            "github",
            MapCodespaceState(codespace.State),
            codespace.WebUrl,
            0, // We don't know active projects without connecting
            codespace.LastUsedAt
        );
    }

    public async Task StartHostAsync(string hostId)
    {
        await StartCodespaceViaRestApi(hostId);
    }

    public async Task StopHostAsync(string hostId)
    {
        await StopCodespaceViaRestApi(hostId);
    }

    public async Task<IProjectConnection> ConnectAsync(string hostId)
    {
        var codespace = await GetCodespaceByName(hostId);

        if (codespace == null)
        {
            throw new InvalidOperationException($"Codespace {hostId} not found");
        }

        if (codespace.State != "Available")
        {
            throw new InvalidOperationException($"Codespace {hostId} is not running");
        }

        // Construct SignalR server URL (assuming standard port 5000)
        var serverUrl = $"{codespace.WebUrl}:5000/projecthub";

        var connection = new SignalRProjectConnection(serverUrl);
        await connection.ConnectAsync();

        return connection;
    }

    private static HostState MapCodespaceState(string state)
    {
        return state switch
        {
            "Available" => HostState.Running,
            "Unavailable" => HostState.Stopped,
            "Starting" => HostState.Starting,
            "Stopping" => HostState.Stopping,
            _ => HostState.Unknown
        };
    }

    // REST API helpers (since Octokit doesn't have native Codespaces support)

    private async Task<List<CodespaceInfo>> GetCodespacesViaRestApi()
    {
        var connection = _client.Connection;
        var endpoint = new Uri($"https://api.github.com/user/codespaces");

        var response = await connection.Get<CodespacesResponse>(endpoint, null);

        return response.Body.Codespaces;
    }

    private async Task<CodespaceInfo?> GetCodespaceByName(string name)
    {
        var codespaces = await GetCodespacesViaRestApi();
        return codespaces.FirstOrDefault(c => c.Name == name);
    }

    private async Task StartCodespaceViaRestApi(string name)
    {
        var connection = _client.Connection;
        var endpoint = new Uri($"https://api.github.com/user/codespaces/{name}/start");

        await connection.Post(endpoint);
    }

    private async Task StopCodespaceViaRestApi(string name)
    {
        var connection = _client.Connection;
        var endpoint = new Uri($"https://api.github.com/user/codespaces/{name}/stop");

        await connection.Post(endpoint);
    }

    // DTOs for GitHub Codespaces API

    private class CodespacesResponse
    {
        public List<CodespaceInfo> Codespaces { get; set; } = new();
    }

    private class CodespaceInfo
    {
        public string Name { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public string State { get; set; } = string.Empty;
        public string WebUrl { get; set; } = string.Empty;
        public DateTime? LastUsedAt { get; set; }
    }
}
