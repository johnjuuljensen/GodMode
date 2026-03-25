using GodMode.ClientBase.Abstractions;
using GodMode.ClientBase.Providers;
using GodMode.ClientBase.Services.Models;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Manages HubConnections to servers. Creates providers from server registrations,
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

    public async Task<IEnumerable<ServerInfo>> ListAllServersAsync()
    {
        var providers = await GetAllProvidersAsync();
        var allServers = new List<ServerInfo>();

        foreach (var (provider, idx) in providers)
        {
            try
            {
                var servers = (await provider.ListServersAsync()).ToList();
                _logger.LogInformation("Provider {Type}[{Index}] discovered {Count} servers: [{Servers}]",
                    provider.Type, idx, servers.Count,
                    string.Join(", ", servers.Select(s => $"{s.Name}({s.State})")));
                allServers.AddRange(servers);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing servers from provider {Type}[{Index}]", provider.Type, idx);
            }
        }

        _logger.LogInformation("Total discovered: {Count} servers", allServers.Count);
        return allServers;
    }

    public HubConnection? GetConnection(string serverId) =>
        _activeConnections.TryGetValue(serverId, out var c) && c.State == HubConnectionState.Connected ? c : null;

    public async Task<HubConnection> ConnectToServerAsync(string serverId)
    {
        _logger.LogInformation("ConnectToServer: {ServerId}", serverId);

        if (_activeConnections.TryGetValue(serverId, out var existing))
        {
            if (existing.State == HubConnectionState.Connected)
            {
                _logger.LogDebug("Already connected to {ServerId}", serverId);
                return existing;
            }

            _logger.LogDebug("Disposing stale connection to {ServerId} (state={State})", serverId, existing.State);
            await existing.DisposeAsync();
            _activeConnections.Remove(serverId);
        }

        var providers = await GetAllProvidersAsync();
        IServerProvider? targetProvider = null;

        foreach (var (provider, _) in providers)
        {
            var servers = await provider.ListServersAsync();
            if (servers.Any(s => s.Id == serverId))
            {
                targetProvider = provider;
                break;
            }
        }

        if (targetProvider == null)
            throw new InvalidOperationException($"Server {serverId} not found in any registered provider");

        var connection = await ConnectWithRetryAsync(targetProvider, serverId);
        _activeConnections[serverId] = connection;
        _logger.LogInformation("Connected to {ServerId} via {ProviderType}", serverId, targetProvider.Type);
        return connection;
    }

    public async Task DisconnectFromServerAsync(string serverId)
    {
        if (_activeConnections.TryGetValue(serverId, out var connection))
        {
            _logger.LogInformation("Disconnecting from {ServerId}", serverId);
            await connection.DisposeAsync();
            _activeConnections.Remove(serverId);
        }
    }

    public async Task DisconnectAllAsync()
    {
        _logger.LogInformation("Disconnecting all ({Count} connections)", _activeConnections.Count);
        foreach (var connection in _activeConnections.Values)
            await connection.DisposeAsync();
        _activeConnections.Clear();
    }

    public bool IsConnected(string serverId) =>
        _activeConnections.TryGetValue(serverId, out var c) && c.State == HubConnectionState.Connected;

    private async Task<HubConnection> ConnectWithRetryAsync(
        IServerProvider provider, string serverId, int maxRetries = 3)
    {
        Exception? lastException = null;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                _logger.LogDebug("Connection attempt {Attempt}/{Max} to {ServerId}", i + 1, maxRetries, serverId);
                return await provider.ConnectAsync(serverId);
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogWarning(ex, "Connection attempt {Attempt}/{Max} to {ServerId} failed",
                    i + 1, maxRetries, serverId);
                if (i < maxRetries - 1)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)));
            }
        }

        throw new InvalidOperationException(
            $"Failed to connect to server {serverId} after {maxRetries} attempts",
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
