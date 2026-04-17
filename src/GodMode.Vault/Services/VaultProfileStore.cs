using System.Text.Json;
using GodMode.Vault.Models;

namespace GodMode.Vault.Services;

/// <summary>
/// Persists per-user <see cref="VaultProfile"/> setup material.
/// Layout: {basePath}/{sanitizedUserSub}/.vault-profile.json
///
/// The profile contains a salt (public), WebAuthn credential IDs (public), and wrapped-KEK blobs
/// (already encrypted under keys Vault never sees). Vault cannot unwrap these.
/// </summary>
public sealed class VaultProfileStore
{
    private const string ProfileFileName = ".vault-profile.json";

    private readonly string _basePath;
    private readonly ILogger<VaultProfileStore> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public VaultProfileStore(IConfiguration configuration, ILogger<VaultProfileStore> logger)
    {
        _basePath = configuration["Vault:StoragePath"]
            ?? Environment.GetEnvironmentVariable("VAULT_STORAGE_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".godmode-vault", "secrets");
        _logger = logger;
    }

    public async Task<VaultProfile?> GetAsync(string userSub, CancellationToken ct = default)
    {
        var path = GetProfilePath(userSub);
        if (!File.Exists(path)) return null;

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<VaultProfile>(json, JsonOptions);
    }

    public async Task StoreAsync(string userSub, VaultProfile profile, CancellationToken ct = default)
    {
        var userDir = GetUserDir(userSub);
        Directory.CreateDirectory(userDir);

        var path = Path.Combine(userDir, ProfileFileName);
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        await File.WriteAllTextAsync(path, json, ct);

        _logger.LogInformation("Stored vault profile for user {UserSub}", userSub);
    }

    private string GetUserDir(string userSub) =>
        Path.Combine(_basePath, UserIdentity.SanitizeSubForPath(userSub));

    private string GetProfilePath(string userSub) =>
        Path.Combine(GetUserDir(userSub), ProfileFileName);
}
