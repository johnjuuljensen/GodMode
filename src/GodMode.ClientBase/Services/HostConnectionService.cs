using GodMode.ClientBase.Abstractions;
using GodMode.ClientBase.Providers;
using GodMode.ClientBase.Services.Models;
using GodMode.Shared.Models;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Manages connections to hosts and tracks their status.
/// Now uses IServerRegistryService (flat server list) instead of IProfileService.
/// </summary>
public class HostConnectionService : IHostConnectionService
{
    private readonly IServerRegistryService _serverRegistry;
    private readonly Dictionary<string, IHostProvider> _providers = new();
    private readonly Dictionary<string, IProjectConnection> _activeConnections = new();

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
            var server = servers[i];
            var provider = CreateProvider(server);
            if (provider != null)
            {
                var key = $"{server.Type}:{server.Username ?? server.Url}";
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
                var hosts = await provider.ListHostsAsync();
                allHosts.AddRange(hosts);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error listing hosts from provider {provider.Type}: {ex.Message}");
            }
        }

        return allHosts;
    }

    public async Task<IProjectConnection> ConnectToHostAsync(string hostId)
    {
        // Return existing connection if still active
        if (_activeConnections.TryGetValue(hostId, out var existingConnection))
        {
            if (existingConnection.IsConnected)
                return existingConnection;

            existingConnection.Dispose();
            _activeConnections.Remove(hostId);
        }

        // Find the provider that owns this host
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

    public void DisconnectFromHost(string hostId)
    {
        if (_activeConnections.TryGetValue(hostId, out var connection))
        {
            connection.Disconnect();
            connection.Dispose();
            _activeConnections.Remove(hostId);
        }
    }

    public void DisconnectAll()
    {
        foreach (var connection in _activeConnections.Values)
        {
            connection.Disconnect();
            connection.Dispose();
        }
        _activeConnections.Clear();
    }

    public bool IsConnected(string hostId)
    {
        return _activeConnections.TryGetValue(hostId, out var connection) && connection.IsConnected;
    }

    // Backward-compat overloads — profileName is ignored

    public Task<IEnumerable<(IHostProvider Provider, int ServerIndex)>> GetProvidersForProfileAsync(string profileName)
        => GetAllProvidersAsync();

    public Task<IEnumerable<HostInfo>> ListAllHostsAsync(string profileName)
        => ListAllHostsAsync();

    public Task<IProjectConnection> ConnectToHostAsync(string profileName, string hostId)
        => ConnectToHostAsync(hostId);

    public void DisconnectFromHost(string profileName, string hostId)
        => DisconnectFromHost(hostId);

    public bool IsConnected(string profileName, string hostId)
        => IsConnected(hostId);

    private async Task<IProjectConnection> ConnectWithRetryAsync(
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
                Console.WriteLine($"Connection attempt {i + 1} failed: {ex.Message}");
                if (i < maxRetries - 1)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)));
            }
        }

        throw new InvalidOperationException(
            $"Failed to connect to host {hostId} after {maxRetries} attempts",
            lastException);
    }

    private IHostProvider? CreateProvider(ServerRegistration server)
    {
        return server.Type switch
        {
            "github" => CreateGitHubProvider(server),
            "local" => CreateLocalProvider(server),
            _ => null
        };
    }

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

        if (string.IsNullOrEmpty(serverUrl))
            serverUrl = "http://localhost:31337";
        else if (!serverUrl.StartsWith("http://") && !serverUrl.StartsWith("https://"))
            serverUrl = "http://localhost:31337";

        var hostName = server.DisplayName ?? "Local Server";
        return new LocalFolderProvider(serverUrl, hostName);
    }
}
