using System.Net.Http.Json;
using GodMode.ClientBase.Abstractions;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Octokit;

namespace GodMode.ClientBase.Providers;

/// <summary>
/// Host provider for GitHub Codespaces running GodMode.Server.
/// </summary>
public class GitHubCodespaceProvider : IHostProvider
{
    private readonly GitHubClient _client;
    private readonly string _token;
    private readonly ILogger _logger;

    public string Type => "github";

    public GitHubCodespaceProvider(string token, string username, ILoggerFactory? loggerFactory = null)
    {
        _token = token;
        _logger = (loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)
                  .CreateLogger<GitHubCodespaceProvider>();
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
            _logger.LogDebug("GitHub API returned {Count} codespaces", codespaces.Count);

            var running = codespaces.Where(c => c.State == "Available").ToList();
            var notRunning = codespaces.Where(c => c.State != "Available").ToList();

            foreach (var c in codespaces)
                _logger.LogDebug("  Codespace: {Name} ({DisplayName}) state={State}", c.Name, c.DisplayName, c.State);

            var probeResults = await Task.WhenAll(
                running.Select(async c => (Codespace: c, HasGodMode: await ProbeGodModeServerAsync(c.Name, _token))));

            foreach (var (codespace, hasGodMode) in probeResults)
                _logger.LogDebug("  Probe {Name}: hasGodMode={HasGodMode}", codespace.Name, hasGodMode);

            foreach (var (codespace, _) in probeResults.Where(r => r.HasGodMode))
                hosts.Add(ToHostInfo(codespace, HostState.Running));

            foreach (var (codespace, _) in probeResults.Where(r =>
                !r.HasGodMode && (r.Codespace.DisplayName ?? r.Codespace.Name).Contains("godmode", StringComparison.OrdinalIgnoreCase)))
                hosts.Add(ToHostInfo(codespace, HostState.Running));

            foreach (var codespace in notRunning.Where(c =>
                (c.DisplayName ?? c.Name).Contains("godmode", StringComparison.OrdinalIgnoreCase)))
            {
                var mapped = MapCodespaceState(codespace.State);
                _logger.LogDebug("  Non-running godmode codespace: {Name} ghState={GhState} mapped={Mapped}",
                    codespace.Name, codespace.State, mapped);
                hosts.Add(ToHostInfo(codespace, mapped));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing codespaces");
        }

        _logger.LogInformation("GitHub provider found {Count} godmode hosts", hosts.Count);
        return hosts;
    }

    public async Task<HostStatus> GetHostStatusAsync(string hostId)
    {
        var codespace = await GetCodespaceByName(hostId)
            ?? throw new InvalidOperationException($"Codespace {hostId} not found");

        return new HostStatus(codespace.Name, codespace.DisplayName ?? codespace.Name,
            "github", MapCodespaceState(codespace.State), codespace.WebUrl, 0, codespace.LastUsedAt);
    }

    public async Task StartHostAsync(string hostId)
    {
        _logger.LogInformation("Starting codespace {HostId}", hostId);
        await StartCodespaceViaRestApi(hostId);
    }

    public async Task StopHostAsync(string hostId)
    {
        _logger.LogInformation("Stopping codespace {HostId}", hostId);
        await StopCodespaceViaRestApi(hostId);
    }

    public async Task<HubConnection> ConnectAsync(string hostId)
    {
        var codespace = await GetCodespaceByName(hostId)
            ?? throw new InvalidOperationException($"Codespace {hostId} not found");

        if (codespace.State != "Available")
            throw new InvalidOperationException($"Codespace {hostId} is not running (state={codespace.State})");

        var serverUrl = $"https://{codespace.Name}-31337.app.github.dev";
        _logger.LogInformation("Connecting to codespace {HostId} at {Url}", hostId, serverUrl);

        var hubUrl = serverUrl.TrimEnd('/') + "/hubs/projects";
        var builder = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(_token);
            })
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

    private static HostInfo ToHostInfo(CodespaceInfo c, HostState state)
    {
        var description = c.RepositoryFullName;
        if (!string.IsNullOrEmpty(c.Branch))
            description = $"{description} · {c.Branch}";
        return new HostInfo(c.Name, c.DisplayName ?? c.Name, "github", state,
            $"https://{c.Name}-31337.app.github.dev", description);
    }

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

    private static HostState MapCodespaceState(string state) => state switch
    {
        "Available" => HostState.Running,
        "Shutdown" or "Unavailable" => HostState.Stopped,
        "Starting" or "Queued" or "Created" or "Provisioning" or "Rebuilding" => HostState.Starting,
        "ShuttingDown" or "Stopping" => HostState.Stopping,
        _ => HostState.Unknown
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
        public CodespaceRepository? Repository { get; set; }
        public CodespaceGitStatus? GitStatus { get; set; }
        public string RepositoryFullName => Repository?.FullName ?? "";
        public string? Branch => GitStatus?.Ref;
    }
    private class CodespaceRepository { public string FullName { get; set; } = ""; }
    private class CodespaceGitStatus { public string? Ref { get; set; } }
}
