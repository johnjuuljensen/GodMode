using GodMode.ClientBase.Abstractions;
using GodMode.ClientBase.Providers;
using GodMode.ClientBase.Services.Models;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Manages HubConnections to hosts. Creates providers from server registrations,
/// handles connection lifecycle with retry logic.
/// </summary>
public class HostConnectionService : IHostConnectionService
{
    private readonly IServerRegistryService _serverRegistry;
    private readonly Dictionary<string, IHostProvider> _providers = new();
    private readonly Dictionary<string, HubConnection> _activeConnections = new();

    public HostConnectionService(IServerRegistryService serverRegistry)
    {
        _serverRegistry = serverRegistry;
    }

    public async Task<IEnumerable<(IHostProvider Provider, int ServerIndex)>> GetAllProvidersAsync()
    {
        var servers = await _serverRegistry.GetServersAsync();
        var providers = new List<(IHostProvider Provider, int ServerIndex)>();

        for (int i = 0; i < servers.Count; i++)
        {
            var provider = CreateProvider(servers[i]);
            if (provider != null)
            {
                var key = $"{servers[i].Type}:{servers[i].Username ?? servers[i].Url}";
                _providers[key] = provider;
                providers.Add((provider, i));
            }
        }

        return providers;
    }

    public async Task<IEnumerable<HostInfo>> ListAllHostsAsync()
    {
        var providers = await GetAllProvidersAsync();
        var allHosts = new List<HostInfo>();

        foreach (var (provider, _) in providers)
        {
            try
            {
                allHosts.AddRange(await provider.ListHostsAsync());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error listing hosts from provider {provider.Type}: {ex.Message}");
            }
        }

        return allHosts;
    }

    public HubConnection? GetConnection(string hostId) =>
        _activeConnections.TryGetValue(hostId, out var c) && c.State == HubConnectionState.Connected ? c : null;

    public async Task<HubConnection> ConnectToHostAsync(string hostId)
    {
        if (_activeConnections.TryGetValue(hostId, out var existing))
        {
            if (existing.State == HubConnectionState.Connected)
                return existing;

            await existing.DisposeAsync();
            _activeConnections.Remove(hostId);
        }

        var providers = await GetAllProvidersAsync();
        IHostProvider? targetProvider = null;

        foreach (var (provider, _) in providers)
        {
            var hosts = await provider.ListHostsAsync();
            if (hosts.Any(h => h.Id == hostId))
            {
                targetProvider = provider;
                break;
            }
        }

        if (targetProvider == null)
            throw new InvalidOperationException($"Host {hostId} not found in any registered server");

        var connection = await ConnectWithRetryAsync(targetProvider, hostId);
        _activeConnections[hostId] = connection;
        return connection;
    }

    public async Task DisconnectFromHostAsync(string hostId)
    {
        if (_activeConnections.TryGetValue(hostId, out var connection))
        {
            await connection.DisposeAsync();
            _activeConnections.Remove(hostId);
        }
    }

    public async Task DisconnectAllAsync()
    {
        foreach (var connection in _activeConnections.Values)
            await connection.DisposeAsync();
        _activeConnections.Clear();
    }

    public bool IsConnected(string hostId) =>
        _activeConnections.TryGetValue(hostId, out var c) && c.State == HubConnectionState.Connected;

    private static async Task<HubConnection> ConnectWithRetryAsync(
        IHostProvider provider, string hostId, int maxRetries = 3)
    {
        Exception? lastException = null;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                return await provider.ConnectAsync(hostId);
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (i < maxRetries - 1)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)));
            }
        }

        throw new InvalidOperationException(
            $"Failed to connect to host {hostId} after {maxRetries} attempts",
            lastException);
    }

    private IHostProvider? CreateProvider(ServerRegistration server) => server.Type switch
    {
        "github" => CreateGitHubProvider(server),
        "local" => CreateLocalProvider(server),
        _ => null
    };

    private IHostProvider? CreateGitHubProvider(ServerRegistration server)
    {
        if (string.IsNullOrEmpty(server.Token) || string.IsNullOrEmpty(server.Username))
            return null;

        var decryptedToken = _serverRegistry.DecryptToken(server.Token);
        return new GitHubCodespaceProvider(decryptedToken, server.Username);
    }

    private IHostProvider? CreateLocalProvider(ServerRegistration server)
    {
        var serverUrl = server.Url;
        if (string.IsNullOrEmpty(serverUrl) || (!serverUrl.StartsWith("http://") && !serverUrl.StartsWith("https://")))
            serverUrl = "http://localhost:31337";

        return new LocalFolderProvider(serverUrl, server.DisplayName ?? "Local Server");
    }
}
