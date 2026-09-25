using GodMode.ClientBase.Services.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Manages server registrations stored in servers.json, with access tokens in an <see cref="ISecretStore"/>.
/// </summary>
public class ServerRegistryService : IServerRegistryService
{
    private const string ServersFileName = "servers.json";
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
        };

        await _lock.WaitAsync();
        try
        {
            var servers = await LoadAsync();
            if (!string.IsNullOrEmpty(accessToken))
                await StoreTokenAsync(added.Id, accessToken);
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

    public Task<string?> GetAccessTokenAsync(string id) => _secrets.GetAsync(TokenKey(id));

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Puts a token in secure storage. It has nowhere else to go, so a store that refuses it fails the add.</summary>
    private async Task StoreTokenAsync(string id, string token)
    {
        try
        {
            await _secrets.SetAsync(TokenKey(id), token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Secure storage refused the access token of server {Id}", id);
            throw new InvalidOperationException(
                $"The server was not added: this device's secure storage would not keep its access token ({ex.Message}).", ex);
        }
    }

    private async Task<IReadOnlyList<ServerRegistration>> LoadAsync() =>
        _cached ??= File.Exists(_serversPath) ? await ReadServersFileAsync() : [];

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

    private async Task SaveAsync(IReadOnlyList<ServerRegistration> servers)
    {
        Directory.CreateDirectory(_appDataPath);
        var json = JsonSerializer.Serialize(new ServersConfig { Servers = servers }, WriteOptions);
        await File.WriteAllTextAsync(_serversPath, json);
        _cached = servers;
    }
}
