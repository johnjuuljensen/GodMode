using GodMode.ClientBase.Abstractions;
using GodMode.ClientBase.Providers;
using GodMode.ClientBase.Services.Models;
using GodMode.Shared.Models;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Manages connections to hosts and tracks their status
/// </summary>
public class HostConnectionService : IHostConnectionService
{
    private readonly IProfileService _profileService;
    private readonly Dictionary<string, IHostProvider> _providers = new();
    private readonly Dictionary<string, IProjectConnection> _activeConnections = new();

    public HostConnectionService(IProfileService profileService)
    {
        _profileService = profileService;
    }

    public async Task<IEnumerable<IHostProvider>> GetProvidersForProfileAsync(string profileName)
    {
        var profile = await _profileService.GetProfileAsync(profileName);

        if (profile == null)
            return Enumerable.Empty<IHostProvider>();

        var providers = new List<IHostProvider>();

        foreach (var server in profile.Servers)
        {
            var provider = CreateProvider(server);
            if (provider != null)
            {
                _providers[server.Id] = provider;
                providers.Add(provider);
            }
        }

        return providers;
    }

    public async Task<IEnumerable<HostInfo>> ListAllHostsAsync(string profileName)
    {
        var providers = await GetProvidersForProfileAsync(profileName);
        var allHosts = new List<HostInfo>();

        foreach (var provider in providers)
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

    public async Task<IProjectConnection> ConnectToHostAsync(string profileName, string hostId)
    {
        var connectionKey = $"{profileName}:{hostId}";

        // Return existing connection if still active
        if (_activeConnections.TryGetValue(connectionKey, out var existingConnection))
        {
            if (existingConnection.IsConnected)
            {
                return existingConnection;
            }

            // Clean up dead connection
            existingConnection.Dispose();
            _activeConnections.Remove(connectionKey);
        }

        // Find the provider that owns this host
        var providers = await GetProvidersForProfileAsync(profileName);
        IHostProvider? targetProvider = null;

        foreach (var provider in providers)
        {
            var hosts = await provider.ListHostsAsync();
            if (hosts.Any(h => h.Id == hostId))
            {
                targetProvider = provider;
                break;
            }
        }

        if (targetProvider == null)
        {
            throw new InvalidOperationException($"Host {hostId} not found in profile {profileName}");
        }

        // Connect with retry logic
        var connection = await ConnectWithRetryAsync(targetProvider, hostId, connectionKey);
        _activeConnections[connectionKey] = connection;

        return connection;
    }

    public void DisconnectFromHost(string profileName, string hostId)
    {
        var connectionKey = $"{profileName}:{hostId}";

        if (_activeConnections.TryGetValue(connectionKey, out var connection))
        {
            connection.Disconnect();
            connection.Dispose();
            _activeConnections.Remove(connectionKey);
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

    public bool IsConnected(string profileName, string hostId)
    {
        var connectionKey = $"{profileName}:{hostId}";
        return _activeConnections.TryGetValue(connectionKey, out var connection) && connection.IsConnected;
    }

    private async Task<IProjectConnection> ConnectWithRetryAsync(
        IHostProvider provider,
        string hostId,
        string connectionKey,
        int maxRetries = 3)
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
                {
                    // Exponential backoff: 1s, 2s between retries
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)));
                }
            }
        }

        throw new InvalidOperationException(
            $"Failed to connect to host {hostId} after {maxRetries} attempts",
            lastException);
    }

    private IHostProvider? CreateProvider(ServerConfig server)
    {
        return server.Type switch
        {
            "github" => CreateGitHubProvider(server),
            "local" => CreateLocalProvider(server),
            _ => null
        };
    }

    private IHostProvider? CreateGitHubProvider(ServerConfig server)
    {
        if (string.IsNullOrEmpty(server.Token) || string.IsNullOrEmpty(server.Username))
            return null;

        var decryptedToken = _profileService.DecryptToken(server.Token);
        return new GitHubCodespaceProvider(decryptedToken, server.Username);
    }

    private IHostProvider? CreateLocalProvider(ServerConfig server)
    {
        var serverUrl = server.Path;

        if (string.IsNullOrEmpty(serverUrl))
            serverUrl = "http://localhost:31337";
        else if (!serverUrl.StartsWith("http://") && !serverUrl.StartsWith("https://"))
            serverUrl = "http://localhost:31337";

        var hostName = server.DisplayName
            ?? server.Metadata?.GetValueOrDefault("name")
            ?? "Local Server";
        return new LocalFolderProvider(serverUrl, hostName, server.Id);
    }
}
