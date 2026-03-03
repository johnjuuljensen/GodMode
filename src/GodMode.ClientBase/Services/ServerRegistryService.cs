using GodMode.ClientBase.Services.Models;
using System.Text.Json;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Manages server registrations stored in ~/.godmode/servers.json.
/// On first load, migrates from profiles.json if servers.json doesn't exist.
/// </summary>
public class ServerRegistryService : IServerRegistryService
{
    private const string ServersFileName = "servers.json";
    private const string ProfilesFileName = "profiles.json";
    private readonly string _serversPath;
    private readonly string _appDataPath;
    private readonly ITokenProtector _tokenProtector;
    private ServersConfig? _cachedConfig;

    public ServerRegistryService(string appDataPath, ITokenProtector tokenProtector)
    {
        _appDataPath = appDataPath;
        _serversPath = Path.Combine(appDataPath, ServersFileName);
        _tokenProtector = tokenProtector;
    }

    public async Task<List<ServerRegistration>> GetServersAsync()
    {
        var config = await LoadConfigAsync();
        return config.Servers;
    }

    public async Task AddServerAsync(ServerRegistration server)
    {
        var config = await LoadConfigAsync();

        // Encrypt token before saving
        var toSave = CloneWithEncryptedToken(server);
        config.Servers.Add(toSave);
        await SaveConfigAsync(config);
    }

    public async Task UpdateServerAsync(int index, ServerRegistration server)
    {
        var config = await LoadConfigAsync();
        if (index < 0 || index >= config.Servers.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        config.Servers[index] = CloneWithEncryptedToken(server);
        await SaveConfigAsync(config);
    }

    public async Task RemoveServerAsync(int index)
    {
        var config = await LoadConfigAsync();
        if (index < 0 || index >= config.Servers.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        config.Servers.RemoveAt(index);
        await SaveConfigAsync(config);
    }

    public bool IsDuplicate(ServerRegistration server, int? excludeIndex = null)
    {
        var config = _cachedConfig ?? new ServersConfig();
        for (int i = 0; i < config.Servers.Count; i++)
        {
            if (i == excludeIndex) continue;
            var existing = config.Servers[i];
            if (!string.Equals(existing.Type, server.Type, StringComparison.OrdinalIgnoreCase)) continue;

            if (server.Type == "local" &&
                NormalizeUrl(existing.Url) == NormalizeUrl(server.Url))
                return true;

            if (server.Type == "github" &&
                string.Equals(existing.Username, server.Username, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public string DecryptToken(string encryptedToken) =>
        _tokenProtector.Unprotect(encryptedToken);

    public string EncryptToken(string token) =>
        _tokenProtector.Protect(token);

    private ServerRegistration CloneWithEncryptedToken(ServerRegistration server)
    {
        return new ServerRegistration
        {
            Type = server.Type,
            Url = server.Url,
            Username = server.Username,
            Token = server.Type == "github" && !string.IsNullOrEmpty(server.Token)
                ? _tokenProtector.Protect(server.Token)
                : server.Token,
            DisplayName = server.DisplayName
        };
    }

    private async Task<ServersConfig> LoadConfigAsync()
    {
        if (_cachedConfig != null)
            return _cachedConfig;

        if (File.Exists(_serversPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_serversPath);
                _cachedConfig = JsonSerializer.Deserialize<ServersConfig>(json) ?? new ServersConfig();
            }
            catch
            {
                _cachedConfig = new ServersConfig();
            }
            return _cachedConfig;
        }

        // Migration: extract unique servers from old profiles.json
        _cachedConfig = await MigrateFromProfilesAsync();
        if (_cachedConfig.Servers.Count > 0)
            await SaveConfigAsync(_cachedConfig);

        return _cachedConfig;
    }

    private async Task<ServersConfig> MigrateFromProfilesAsync()
    {
        var profilesPath = Path.Combine(_appDataPath, ProfilesFileName);
        if (!File.Exists(profilesPath))
            return new ServersConfig();

        try
        {
            var json = await File.ReadAllTextAsync(profilesPath);
            var profilesConfig = JsonSerializer.Deserialize<ProfilesConfig>(json);
            if (profilesConfig == null)
                return new ServersConfig();

            var servers = new List<ServerRegistration>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var profile in profilesConfig.Profiles)
            {
                foreach (var account in profile.Accounts)
                {
                    var key = account.Type == "github"
                        ? $"github:{account.Username}"
                        : $"local:{NormalizeUrl(account.Path)}";

                    if (seen.Add(key))
                    {
                        servers.Add(new ServerRegistration
                        {
                            Type = account.Type,
                            Url = account.Type == "local" ? account.Path : null,
                            Username = account.Username,
                            Token = account.Token, // already encrypted
                            DisplayName = account.Metadata?.GetValueOrDefault("name")
                        });
                    }
                }
            }

            return new ServersConfig { Servers = servers };
        }
        catch
        {
            return new ServersConfig();
        }
    }

    private async Task SaveConfigAsync(ServersConfig config)
    {
        _cachedConfig = config;
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_serversPath, json);
    }

    private static string? NormalizeUrl(string? url) =>
        url?.TrimEnd('/').ToLowerInvariant();
}
