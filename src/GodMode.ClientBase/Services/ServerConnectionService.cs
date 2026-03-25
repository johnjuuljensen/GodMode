using GodMode.ClientBase.Abstractions;
using GodMode.ClientBase.Providers;
using GodMode.ClientBase.Services.Models;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Manages HubConnections to servers. Creates providers from server registrations,
/// handles connection lifecycle with retry logic.
/// </summary>
public class ServerConnectionService : IServerConnectionService
{
    private readonly IServerRegistryService _serverRegistry;
    private readonly Dictionary<string, IServerProvider> _providers = new();
    private readonly Dictionary<string, HubConnection> _activeConnections = new();

    public ServerConnectionService(IServerRegistryService serverRegistry)
    {
        _serverRegistry = serverRegistry;
    }

    public async Task<IEnumerable<(IServerProvider Provider, int ServerIndex)>> GetAllProvidersAsync()
    {
        var servers = await _serverRegistry.GetServersAsync();
        var providers = new List<(IServerProvider Provider, int ServerIndex)>();

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

    public async Task<IEnumerable<ServerInfo>> ListAllServersAsync()
    {
        var providers = await GetAllProvidersAsync();
        var allServers = new List<ServerInfo>();

        foreach (var (provider, _) in providers)
        {
            try
            {
                allServers.AddRange(await provider.ListServersAsync());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error listing servers from provider {provider.Type}: {ex.Message}");
            }
        }

        return allServers;
    }

    public HubConnection? GetConnection(string serverId) =>
        _activeConnections.TryGetValue(serverId, out var c) && c.State == HubConnectionState.Connected ? c : null;

    public async Task<HubConnection> ConnectToServerAsync(string serverId)
    {
        if (_activeConnections.TryGetValue(serverId, out var existing))
        {
            if (existing.State == HubConnectionState.Connected)
                return existing;

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
        return connection;
    }

    public async Task DisconnectFromServerAsync(string serverId)
    {
        if (_activeConnections.TryGetValue(serverId, out var connection))
        {
            await connection.DisposeAsync();
            _activeConnections.Remove(serverId);
        }
    }

    public async Task DisconnectAllAsync()
    {
        foreach (var connection in _activeConnections.Values)
            await connection.DisposeAsync();
        _activeConnections.Clear();
    }

    public bool IsConnected(string serverId) =>
        _activeConnections.TryGetValue(serverId, out var c) && c.State == HubConnectionState.Connected;

    private static async Task<HubConnection> ConnectWithRetryAsync(
        IServerProvider provider, string serverId, int maxRetries = 3)
    {
        Exception? lastException = null;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                return await provider.ConnectAsync(serverId);
            }
            catch (Exception ex)
            {
                lastException = ex;
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
        return new GitHubCodespaceProvider(decryptedToken, server.Username);
    }

    private IServerProvider? CreateLocalProvider(ServerRegistration server)
    {
        if (string.IsNullOrEmpty(server.Url) || (!server.Url.StartsWith("http://") && !server.Url.StartsWith("https://")))
            return null;

        return new LocalFolderProvider(server.Url, server.DisplayName ?? "Local Server");
    }
}
