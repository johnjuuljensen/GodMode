using System.Text.Json;
using System.Text.RegularExpressions;
using GodMode.Vault.Models;

namespace GodMode.Vault.Services;

/// <summary>
/// File-based encrypted secret store.
/// Layout: {basePath}/{userSub}/{profile}/{secretName}        — encrypted binary
///         {basePath}/{userSub}/{profile}/{secretName}.meta.json — metadata sidecar
/// </summary>
public sealed partial class FileSecretStore
{
    private readonly string _basePath;
    private readonly EncryptionService _encryption;
    private readonly ILogger<FileSecretStore> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FileSecretStore(IConfiguration configuration, EncryptionService encryption, ILogger<FileSecretStore> logger)
    {
        _basePath = configuration["Vault:StoragePath"]
            ?? Environment.GetEnvironmentVariable("VAULT_STORAGE_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".godmode-vault", "secrets");
        _encryption = encryption;
        _logger = logger;
    }

    public async Task StoreAsync(string userSub, string profile, string secretName, byte[] value, TimeSpan? ttl, CancellationToken ct = default)
    {
        ValidateName(secretName);
        var dir = GetSecretDir(userSub, profile);
        Directory.CreateDirectory(dir);

        var encrypted = _encryption.Encrypt(value, userSub);
        var secretPath = Path.Combine(dir, secretName);
        var metaPath = secretPath + ".meta.json";

        var metadata = new SecretMetadata
        {
            Name = secretName,
            CreatedAt = DateTimeOffset.UtcNow,
            Ttl = ttl
        };

        await File.WriteAllBytesAsync(secretPath, encrypted, ct);
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(metadata, JsonOptions), ct);

        _logger.LogInformation("Stored secret {Profile}/{Secret} for user {UserSub}", profile, secretName, userSub);
    }

    public async Task<byte[]?> GetAsync(string userSub, string profile, string secretName, CancellationToken ct = default)
    {
        ValidateName(secretName);
        var secretPath = Path.Combine(GetSecretDir(userSub, profile), secretName);
        if (!File.Exists(secretPath)) return null;

        var meta = await GetMetadataAsync(userSub, profile, secretName, ct);
        if (meta?.IsExpired == true)
        {
            _logger.LogInformation("Secret {Profile}/{Secret} expired for user {UserSub}", profile, secretName, userSub);
            return null;
        }

        var encrypted = await File.ReadAllBytesAsync(secretPath, ct);
        return _encryption.Decrypt(encrypted, userSub);
    }

    public async Task<SecretMetadata?> GetMetadataAsync(string userSub, string profile, string secretName, CancellationToken ct = default)
    {
        ValidateName(secretName);
        var metaPath = Path.Combine(GetSecretDir(userSub, profile), secretName + ".meta.json");
        if (!File.Exists(metaPath)) return null;

        var json = await File.ReadAllTextAsync(metaPath, ct);
        return JsonSerializer.Deserialize<SecretMetadata>(json, JsonOptions);
    }

    public void Delete(string userSub, string profile, string secretName)
    {
        ValidateName(secretName);
        var dir = GetSecretDir(userSub, profile);
        var secretPath = Path.Combine(dir, secretName);
        var metaPath = secretPath + ".meta.json";

        if (File.Exists(secretPath)) File.Delete(secretPath);
        if (File.Exists(metaPath)) File.Delete(metaPath);

        _logger.LogInformation("Deleted secret {Profile}/{Secret} for user {UserSub}", profile, secretName, userSub);
    }

    public async Task<ProfileCheckResult> CheckProfileAsync(string userSub, string profile, IReadOnlyList<string> requiredSecrets, CancellationToken ct = default)
    {
        foreach (var name in requiredSecrets) ValidateName(name);

        var statuses = new List<SecretStatus>();

        foreach (var name in requiredSecrets)
        {
            var meta = await GetMetadataAsync(userSub, profile, name, ct);
            var secretPath = Path.Combine(GetSecretDir(userSub, profile), name);
            var exists = File.Exists(secretPath) && meta != null;
            var expired = meta?.IsExpired ?? false;

            statuses.Add(new SecretStatus(name, exists && !expired, expired, meta?.ExpiresAt));
        }

        var ready = statuses.All(s => s.Exists);
        return new ProfileCheckResult(profile, ready, statuses);
    }

    public async Task<IDictionary<string, byte[]>> GetProfileSecretsAsync(string userSub, string profile, IReadOnlyList<string> secretNames, CancellationToken ct = default)
    {
        var result = new Dictionary<string, byte[]>();

        foreach (var name in secretNames)
        {
            var value = await GetAsync(userSub, profile, name, ct);
            if (value != null)
                result[name] = value;
        }

        return result;
    }

    public IReadOnlyList<string> ListProfiles(string userSub)
    {
        var userDir = Path.Combine(_basePath, SanitizeSub(userSub));
        if (!Directory.Exists(userDir)) return [];

        return Directory.GetDirectories(userDir)
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Cast<string>()
            .ToList();
    }

    public IReadOnlyList<string> ListSecrets(string userSub, string profile)
    {
        var dir = GetSecretDir(userSub, profile);
        if (!Directory.Exists(dir)) return [];

        return Directory.GetFiles(dir)
            .Select(Path.GetFileName)
            .Where(n => n != null && !n.EndsWith(".meta.json", StringComparison.Ordinal))
            .Cast<string>()
            .ToList();
    }

    private string GetSecretDir(string userSub, string profile) =>
        Path.Combine(_basePath, SanitizeSub(userSub), ValidateName(profile));

    /// <summary>Validates that a profile or secret name contains only [a-zA-Z0-9_-].</summary>
    public static string ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!SafeNameRegex().IsMatch(name))
            throw new ArgumentException($"Name '{name}' contains invalid characters. Only a-z, A-Z, 0-9, underscore, and hyphen are allowed.");
        return name;
    }

    /// <summary>Sanitizes user sub (provider:id) into a safe directory name.</summary>
    private static string SanitizeSub(string userSub) => UserIdentity.SanitizeSubForPath(userSub);

    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$")]
    private static partial Regex SafeNameRegex();
}
