using GodMode.Maui.Abstractions;
using GodMode.Maui.Providers;
using GodMode.Shared.Models;
using GodMode.Shared.Enums;

namespace GodMode.Maui.Services;

/// <summary>
/// Manages connections to hosts and tracks their status
/// </summary>
public class HostConnectionService
{
    private readonly ProfileService _profileService;
    private readonly Dictionary<string, IHostProvider> _providers = new();
    private readonly Dictionary<string, IProjectConnection> _activeConnections = new();
    private readonly Dictionary<string, DateTime> _lastConnectionAttempt = new();

    public HostConnectionService(ProfileService profileService)
    {
        _profileService = profileService;
    }

    public async Task<IEnumerable<IHostProvider>> GetProvidersForProfileAsync(string profileName)
    {
        var profile = await _profileService.GetProfileAsync(profileName);

        if (profile == null)
        {
            return Enumerable.Empty<IHostProvider>();
        }

        var providers = new List<IHostProvider>();

        foreach (var account in profile.Accounts)
        {
            var provider = CreateProvider(account);
            if (provider != null)
            {
                var key = $"{profileName}:{account.Type}:{account.Username ?? account.Path}";
                _providers[key] = provider;
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
        // Check if we recently attempted to connect (throttle rapid retries)
        if (_lastConnectionAttempt.TryGetValue(connectionKey, out var lastAttempt))
        {
            var timeSinceLastAttempt = DateTime.UtcNow - lastAttempt;
            if (timeSinceLastAttempt < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(TimeSpan.FromSeconds(5) - timeSinceLastAttempt);
            }
        }

        Exception? lastException = null;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                _lastConnectionAttempt[connectionKey] = DateTime.UtcNow;
                return await provider.ConnectAsync(hostId);
            }
            catch (Exception ex)
            {
                lastException = ex;
                Console.WriteLine($"Connection attempt {i + 1} failed: {ex.Message}");

                if (i < maxRetries - 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i))); // Exponential backoff
                }
            }
        }

        throw new InvalidOperationException(
            $"Failed to connect to host {hostId} after {maxRetries} attempts",
            lastException);
    }

    private IHostProvider? CreateProvider(Models.Account account)
    {
        return account.Type switch
        {
            "github" => CreateGitHubProvider(account),
            "local" => CreateLocalProvider(account),
            _ => null
        };
    }

    private IHostProvider? CreateGitHubProvider(Models.Account account)
    {
        if (string.IsNullOrEmpty(account.Token) || string.IsNullOrEmpty(account.Username))
        {
            return null;
        }

        var decryptedToken = _profileService.DecryptToken(account.Token);
        return new GitHubCodespaceProvider(decryptedToken, account.Username);
    }

    private IHostProvider? CreateLocalProvider(Models.Account account)
    {
        if (string.IsNullOrEmpty(account.Path))
        {
            return null;
        }

        var hostName = account.Metadata?.GetValueOrDefault("name") ?? Path.GetFileName(account.Path);
        return new LocalFolderProvider(account.Path, hostName);
    }
}
