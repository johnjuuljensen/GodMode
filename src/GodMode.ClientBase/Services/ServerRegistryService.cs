using GodMode.ClientBase.Services.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Manages server registrations stored in servers.json, with access tokens in an <see cref="ISecretStore"/>.
/// On load, older files are upgraded in place: entries get an ID, a single Url becomes Urls,
/// and a token found in the file moves to secure storage. profiles.json is migrated when servers.json doesn't exist.
/// </summary>
public class ServerRegistryService : IServerRegistryService
{
    private const string ServersFileName = "servers.json";
    private const string ProfilesFileName = "profiles.json";
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _serversPath;
    private readonly string _appDataPath;
    private readonly ISecretStore _secrets;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<ServerRegistration>? _cached;

    public ServerRegistryService(string appDataPath, ISecretStore secrets, ILogger<ServerRegistryService>? logger = null)
    {
        _appDataPath = appDataPath;
        _serversPath = Path.Combine(appDataPath, ServersFileName);
        _secrets = secrets;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>The secure-storage key holding a registration's access token.</summary>
    public static string TokenKey(string id) => $"godmode.server-token.{id}";

    public async Task<IReadOnlyList<ServerRegistration>> GetServersAsync()
    {
        await _lock.WaitAsync();
        try { return await LoadAsync(); }
        finally { _lock.Release(); }
    }

    public async Task<ServerRegistration> AddServerAsync(ServerRegistration server, string? accessToken)
    {
        var added = server with
        {
            Id = NewId(),
            Urls = server.Urls.Select(u => u.Trim().TrimEnd('/')).Where(u => u.Length > 0).ToList(),
            Url = null,
            Token = null,
        };

        await _lock.WaitAsync();
        try
        {
            var servers = await LoadAsync();
            if (!string.IsNullOrEmpty(accessToken))
                await _secrets.SetAsync(TokenKey(added.Id), accessToken);
            await SaveAsync([.. servers, added]);
            return added;
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> RemoveServerAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var servers = await LoadAsync();
            if (servers.All(s => s.Id != id)) return false;
            await SaveAsync(servers.Where(s => s.Id != id).ToList());
            _secrets.Remove(TokenKey(id));
            return true;
        }
        finally { _lock.Release(); }
    }

    public async Task<string?> GetAccessTokenAsync(string id)
    {
        if (await _secrets.GetAsync(TokenKey(id)) is { } token)
            return token;
        // An entry whose token could not be moved to secure storage yet still carries it.
        var legacy = (await GetServersAsync()).FirstOrDefault(s => s.Id == id)?.Token;
        return legacy == null ? null : LegacyToken.Unprotect(legacy);
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private async Task<IReadOnlyList<ServerRegistration>> LoadAsync()
    {
        if (_cached != null) return _cached;

        var stored = File.Exists(_serversPath) ? await ReadServersFileAsync() : await MigrateFromProfilesAsync();
        var upgraded = new List<ServerRegistration>(stored.Count);
        foreach (var server in stored)
            upgraded.Add(await UpgradeAsync(server));

        if (!upgraded.SequenceEqual(stored))
            await SaveAsync(upgraded);
        else
            _cached = upgraded;
        return _cached!;
    }

    private async Task<IReadOnlyList<ServerRegistration>> ReadServersFileAsync()
    {
        try
        {
            var json = await File.ReadAllTextAsync(_serversPath);
            return JsonSerializer.Deserialize<ServersConfig>(json)?.Servers ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read {Path}", _serversPath);
            return [];
        }
    }

    /// <summary>Gives a registration written by an older build an ID and Urls, and moves its token to secure storage.</summary>
    private async Task<ServerRegistration> UpgradeAsync(ServerRegistration server)
    {
        if (server.Id.Length > 0 && server.Url == null && server.Token == null)
            return server;

        var upgraded = server with
        {
            Id = server.Id.Length > 0 ? server.Id : NewId(),
            Urls = server.Urls.Count > 0 || string.IsNullOrWhiteSpace(server.Url) ? server.Urls : [server.Url.TrimEnd('/')],
            Url = null,
            Token = null,
        };

        if (string.IsNullOrEmpty(server.Token))
            return upgraded;

        string token;
        try
        {
            token = LegacyToken.Unprotect(server.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dropped the unreadable stored token of server {Id}; add the server again", upgraded.Id);
            return upgraded;
        }

        try
        {
            await _secrets.SetAsync(TokenKey(upgraded.Id), token);
            return upgraded;
        }
        catch (Exception ex)
        {
            // Keep the entry as it was, token included, so the next load tries again.
            _logger.LogError(ex, "Could not move the token of server {Id} to secure storage", upgraded.Id);
            return server with { Id = upgraded.Id };
        }
    }

    private async Task<IReadOnlyList<ServerRegistration>> MigrateFromProfilesAsync()
    {
        var profilesPath = Path.Combine(_appDataPath, ProfilesFileName);
        if (!File.Exists(profilesPath))
            return [];

        try
        {
            var json = await File.ReadAllTextAsync(profilesPath);
            var profilesConfig = JsonSerializer.Deserialize<ProfilesConfig>(json);
            if (profilesConfig == null)
                return [];

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return profilesConfig.Profiles
                .SelectMany(p => p.Accounts)
                .Where(a => seen.Add(a.Type == ServerTypes.GitHub
                    ? $"github:{a.Username}"
                    : $"local:{a.Path?.TrimEnd('/').ToLowerInvariant()}"))
                .Select(a => new ServerRegistration
                {
                    Type = a.Type,
                    Url = a.Type == ServerTypes.Local ? a.Path : null,
                    Username = a.Username,
                    Token = a.Token,
                    DisplayName = a.Metadata?.GetValueOrDefault("name"),
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not migrate {Path}", profilesPath);
            return [];
        }
    }

    private async Task SaveAsync(IReadOnlyList<ServerRegistration> servers)
    {
        Directory.CreateDirectory(_appDataPath);
        var json = JsonSerializer.Serialize(new ServersConfig { Servers = servers }, WriteOptions);
        await File.WriteAllTextAsync(_serversPath, json);
        _cached = servers;
    }
}
