using GodMode.ClientBase.Abstractions;
using GodMode.ClientBase.Providers;
using GodMode.ClientBase.Services.Models;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Manages HubConnections to hosts. Creates providers from server registrations,
/// handles connection lifecycle with retry logic.
/// </summary>
public class ServerConnectionService : IServerConnectionService
{
    private readonly IServerRegistryService _serverRegistry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ServerConnectionService> _logger;
    private readonly Dictionary<string, IServerProvider> _providers = new();
    private readonly Dictionary<string, HubConnection> _activeConnections = new();

    public ServerConnectionService(IServerRegistryService serverRegistry,
        ILoggerFactory loggerFactory, ILogger<ServerConnectionService> logger)
    {
        _serverRegistry = serverRegistry;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public async Task<IEnumerable<(IServerProvider Provider, int ServerIndex)>> GetAllProvidersAsync()
    {
        var servers = await _serverRegistry.GetServersAsync();
        var providers = new List<(IServerProvider Provider, int ServerIndex)>();
        _logger.LogDebug("Building providers for {Count} server registrations", servers.Count);

        for (int i = 0; i < servers.Count; i++)
        {
            var provider = CreateProvider(servers[i]);
            if (provider != null)
            {
                var key = $"{servers[i].Type}:{servers[i].Username ?? servers[i].Url}";
                _providers[key] = provider;
                providers.Add((provider, i));
                _logger.LogDebug("Created {Type} provider for registration {Index} (key={Key})", servers[i].Type, i, key);
            }
            else
            {
                _logger.LogWarning("Failed to create provider for registration {Index} (type={Type}, url={Url})",
                    i, servers[i].Type, servers[i].Url);
            }
        }

        return providers;
    }

    public async Task<IEnumerable<ServerInfo>> ListAllHostsAsync()
    {
        var providers = await GetAllProvidersAsync();
        var allHosts = new List<ServerInfo>();

        foreach (var (provider, idx) in providers)
        {
            try
            {
                var hosts = (await provider.ListHostsAsync()).ToList();
                _logger.LogInformation("Provider {Type}[{Index}] discovered {Count} hosts: [{Hosts}]",
                    provider.Type, idx, hosts.Count,
                    string.Join(", ", hosts.Select(h => $"{h.Name}({h.State})")));
                allHosts.AddRange(hosts);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing hosts from provider {Type}[{Index}]", provider.Type, idx);
            }
        }

        _logger.LogInformation("Total discovered: {Count} hosts", allHosts.Count);
        return allHosts;
    }

    public HubConnection? GetConnection(string hostId) =>
        _activeConnections.TryGetValue(hostId, out var c) && c.State == HubConnectionState.Connected ? c : null;

    public async Task<HubConnection> ConnectToHostAsync(string hostId)
    {
        _logger.LogInformation("ConnectToHost: {HostId}", hostId);

        if (_activeConnections.TryGetValue(hostId, out var existing))
        {
            if (existing.State == HubConnectionState.Connected)
            {
                _logger.LogDebug("Already connected to {HostId}", hostId);
                return existing;
            }

            _logger.LogDebug("Disposing stale connection to {HostId} (state={State})", hostId, existing.State);
            await existing.DisposeAsync();
            _activeConnections.Remove(hostId);
        }

        var providers = await GetAllProvidersAsync();
        IServerProvider? targetProvider = null;

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
            throw new InvalidOperationException($"Host {hostId} not found in any registered provider");

        var connection = await ConnectWithRetryAsync(targetProvider, hostId);
        _activeConnections[hostId] = connection;
        _logger.LogInformation("Connected to {HostId} via {ProviderType}", hostId, targetProvider.Type);
        return connection;
    }

    public async Task DisconnectFromHost(string hostId)
    {
        if (_activeConnections.TryGetValue(hostId, out var connection))
        {
            _logger.LogInformation("Disconnecting from {HostId}", hostId);
            await connection.DisposeAsync();
            _activeConnections.Remove(hostId);
        }
    }

    public async Task DisconnectAllAsync()
    {
        _logger.LogInformation("Disconnecting all ({Count} connections)", _activeConnections.Count);
        foreach (var connection in _activeConnections.Values)
            await connection.DisposeAsync();
        _activeConnections.Clear();
    }

    public bool IsConnected(string hostId) =>
        _activeConnections.TryGetValue(hostId, out var c) && c.State == HubConnectionState.Connected;

    private async Task<HubConnection> ConnectWithRetryAsync(
        IServerProvider provider, string hostId, int maxRetries = 3)
    {
        Exception? lastException = null;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                _logger.LogDebug("Connection attempt {Attempt}/{Max} to {HostId}", i + 1, maxRetries, hostId);
                return await provider.ConnectAsync(hostId);
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogWarning(ex, "Connection attempt {Attempt}/{Max} to {HostId} failed",
                    i + 1, maxRetries, hostId);
                if (i < maxRetries - 1)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)));
            }
        }

        throw new InvalidOperationException(
            $"Failed to connect to host {hostId} after {maxRetries} attempts",
            lastException);
    }

    private IServerProvider? CreateProvider(ServerRegistration server) => server.Type switch
    {
        "github" => CreateGitHubProvider(server),
        "local" => CreateLocalProvider(server),
        _ => null
    };

    private IServerProvider? CreateGitHubProvider(ServerRegistration server)
    {
        if (string.IsNullOrEmpty(server.Token) || string.IsNullOrEmpty(server.Username))
            return null;

        var decryptedToken = _serverRegistry.DecryptToken(server.Token);
        return new GitHubCodespaceProvider(decryptedToken, server.Username, _loggerFactory);
    }

    private IServerProvider? CreateLocalProvider(ServerRegistration server)
    {
        if (string.IsNullOrEmpty(server.Url) || (!server.Url.StartsWith("http://") && !server.Url.StartsWith("https://")))
            return null;

        return new LocalFolderProvider(server.Url, server.DisplayName ?? "Local Server", _loggerFactory);
    }
}
