using System.Net.Http.Json;
using GodMode.ClientBase.Abstractions;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Octokit;

namespace GodMode.ClientBase.Providers;

/// <summary>
/// Server provider for GitHub Codespaces running GodMode.Server.
/// </summary>
public class GitHubCodespaceProvider : IServerProvider
{
    private readonly GitHubClient _client;
    private readonly string _token;

    public string Type => "github";

    public GitHubCodespaceProvider(string token, string username)
    {
        _token = token;
        _client = new GitHubClient(new ProductHeaderValue("GodMode"))
        {
            Credentials = new Credentials(token)
        };
    }

    public async Task<IEnumerable<ServerInfo>> ListServersAsync()
    {
        var servers = new List<ServerInfo>();

        try
        {
            var codespaces = await GetCodespacesViaRestApi();
            var running = codespaces.Where(c => c.State == "Available").ToList();
            var notRunning = codespaces.Where(c => c.State != "Available").ToList();

            var probeResults = await Task.WhenAll(
                running.Select(async c => (Codespace: c, HasGodMode: await ProbeGodModeServerAsync(c.Name, _token))));

            foreach (var (codespace, _) in probeResults.Where(r => r.HasGodMode))
                servers.Add(ToServerInfo(codespace, ServerState.Running));

            foreach (var (codespace, _) in probeResults.Where(r =>
                !r.HasGodMode && (r.Codespace.DisplayName ?? r.Codespace.Name).Contains("godmode", StringComparison.OrdinalIgnoreCase)))
                servers.Add(ToServerInfo(codespace, ServerState.Running));

            foreach (var codespace in notRunning.Where(c =>
                (c.DisplayName ?? c.Name).Contains("godmode", StringComparison.OrdinalIgnoreCase)))
                servers.Add(ToServerInfo(codespace, MapCodespaceState(codespace.State)));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error listing codespaces: {ex.Message}");
        }

        return servers;
    }

    public async Task<ServerStatus> GetServerStatusAsync(string serverId)
    {
        var codespace = await GetCodespaceByName(serverId)
            ?? throw new InvalidOperationException($"Codespace {serverId} not found");

        return new ServerStatus(codespace.Name, codespace.DisplayName ?? codespace.Name,
            "github", MapCodespaceState(codespace.State), codespace.WebUrl, 0, codespace.LastUsedAt);
    }

    public async Task StartServerAsync(string serverId) => await StartCodespaceViaRestApi(serverId);
    public async Task StopServerAsync(string serverId) => await StopCodespaceViaRestApi(serverId);

    public async Task<HubConnection> ConnectAsync(string serverId)
    {
        var codespace = await GetCodespaceByName(serverId)
            ?? throw new InvalidOperationException($"Codespace {serverId} not found");

        if (codespace.State != "Available")
            throw new InvalidOperationException($"Codespace {serverId} is not running");

        var serverUrl = $"https://{codespace.Name}-31337.app.github.dev";
        return await HubConnectionFactory.CreateAndStartAsync(serverUrl, _token);
    }

    private static ServerInfo ToServerInfo(CodespaceInfo c, ServerState state) =>
        new(c.Name, c.DisplayName ?? c.Name, "github", state, $"https://{c.Name}-31337.app.github.dev");

    private static async Task<bool> ProbeGodModeServerAsync(string codespaceName, string token)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var response = await client.GetFromJsonAsync<ServerProbeResponse>(
                $"https://{codespaceName}-31337.app.github.dev/");
            return response?.Service == "GodMode.Server";
        }
        catch { return false; }
    }

    private static ServerState MapCodespaceState(string state) => state switch
    {
        "Available" => ServerState.Running,
        "Unavailable" => ServerState.Stopped,
        "Starting" => ServerState.Starting,
        "Stopping" => ServerState.Stopping,
        _ => ServerState.Unknown
    };

    private async Task<List<CodespaceInfo>> GetCodespacesViaRestApi()
    {
        var response = await _client.Connection.Get<CodespacesResponse>(
            new Uri("https://api.github.com/user/codespaces"), null);
        return response.Body.Codespaces;
    }

    private async Task<CodespaceInfo?> GetCodespaceByName(string name)
    {
        var codespaces = await GetCodespacesViaRestApi();
        return codespaces.FirstOrDefault(c => c.Name == name);
    }

    private async Task StartCodespaceViaRestApi(string name) =>
        await _client.Connection.Post(new Uri($"https://api.github.com/user/codespaces/{name}/start"));

    private async Task StopCodespaceViaRestApi(string name) =>
        await _client.Connection.Post(new Uri($"https://api.github.com/user/codespaces/{name}/stop"));

    private class CodespacesResponse { public List<CodespaceInfo> Codespaces { get; set; } = new(); }
    private class ServerProbeResponse { public string? Service { get; set; } }
    private class CodespaceInfo
    {
        public string Name { get; set; } = "";
        public string? DisplayName { get; set; }
        public string State { get; set; } = "";
        public string WebUrl { get; set; } = "";
        public DateTime? LastUsedAt { get; set; }
    }
}
