using System.Net.Http.Json;
using GodMode.ClientBase.Abstractions;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using Octokit;

namespace GodMode.ClientBase.Providers;

/// <summary>
/// Host provider for GitHub Codespaces
/// </summary>
public class GitHubCodespaceProvider : IHostProvider
{
    private readonly GitHubClient _client;
    private readonly string _username;
    private readonly string _token;

    public string Type => "github";

    public GitHubCodespaceProvider(string token, string username)
    {
        _username = username;
        _token = token;
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
            var codespaces = await GetCodespacesViaRestApi();

            var running = codespaces.Where(c => c.State == "Available").ToList();
            var notRunning = codespaces.Where(c => c.State != "Available").ToList();

            // Probe running codespaces for GodMode.Server in parallel
            var probeResults = await Task.WhenAll(
                running.Select(async c => (Codespace: c, HasGodMode: await ProbeGodModeServerAsync(c.Name))));

            foreach (var (codespace, _) in probeResults.Where(r => r.HasGodMode))
            {
                hosts.Add(new HostInfo(
                    codespace.Name,
                    codespace.DisplayName ?? codespace.Name,
                    "github",
                    HostState.Running,
                    codespace.WebUrl
                ));
            }

            // Running codespaces where probe failed but name matches — still show them
            foreach (var (codespace, _) in probeResults.Where(r =>
                !r.HasGodMode && (r.Codespace.DisplayName ?? r.Codespace.Name).Contains("godmode", StringComparison.OrdinalIgnoreCase)))
            {
                hosts.Add(new HostInfo(
                    codespace.Name,
                    codespace.DisplayName ?? codespace.Name,
                    "github",
                    HostState.Running,
                    codespace.WebUrl
                ));
            }

            // For non-running codespaces, filter by display name containing "godmode"
            foreach (var codespace in notRunning.Where(c =>
                (c.DisplayName ?? c.Name).Contains("godmode", StringComparison.OrdinalIgnoreCase)))
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

        // Construct SignalR server URL using codespace port forwarding format
        var serverUrl = $"https://{codespace.Name}-31337.app.github.dev/hubs/projects";

        // Pass the GitHub token for authentication
        var connection = new SignalRProjectConnection(serverUrl, _token);
        await connection.ConnectAsync();

        return connection;
    }

    private static async Task<bool> ProbeGodModeServerAsync(string codespaceName)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var url = $"https://{codespaceName}-31337.app.github.dev/";
            var response = await client.GetFromJsonAsync<ServerProbeResponse>(url);
            return response?.Service == "GodMode.Server";
        }
        catch
        {
            return false;
        }
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

    private class ServerProbeResponse
    {
        public string? Service { get; set; }
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
