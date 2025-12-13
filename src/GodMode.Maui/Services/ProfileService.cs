using GodMode.Maui.Services.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GodMode.Maui.Services;

/// <summary>
/// Manages user profiles and account configurations
/// </summary>
public class ProfileService
{
    private const string ProfilesFileName = "profiles.json";
    private readonly string _profilesPath;
    private ProfilesConfig? _cachedConfig;
    private readonly byte[] _encryptionKey;

    public ProfileService()
    {
        var appDataPath = FileSystem.AppDataDirectory;
        _profilesPath = Path.Combine(appDataPath, ProfilesFileName);

        // Generate or load encryption key (in production, use secure key storage)
        _encryptionKey = GetOrCreateEncryptionKey();
    }

    public async Task<List<Profile>> GetProfilesAsync()
    {
        var config = await LoadConfigAsync();
        return config.Profiles;
    }

    public async Task<Profile?> GetProfileAsync(string name)
    {
        var profiles = await GetProfilesAsync();
        return profiles.FirstOrDefault(p => p.Name == name);
    }

    public async Task<Profile?> GetSelectedProfileAsync()
    {
        var config = await LoadConfigAsync();

        if (!string.IsNullOrEmpty(config.SelectedProfile))
        {
            return config.Profiles.FirstOrDefault(p => p.Name == config.SelectedProfile);
        }

        return config.Profiles.FirstOrDefault();
    }

    public async Task SaveProfileAsync(Profile profile)
    {
        var config = await LoadConfigAsync();

        var existing = config.Profiles.FirstOrDefault(p => p.Name == profile.Name);
        if (existing != null)
        {
            config.Profiles.Remove(existing);
        }

        // Encrypt GitHub tokens before saving
        var profileToSave = new Profile
        {
            Name = profile.Name,
            Accounts = profile.Accounts.Select(a => new Account
            {
                Type = a.Type,
                Username = a.Username,
                Token = a.Type == "github" && !string.IsNullOrEmpty(a.Token)
                    ? EncryptToken(a.Token)
                    : a.Token,
                Path = a.Path,
                Metadata = a.Metadata
            }).ToList()
        };

        config.Profiles.Add(profileToSave);
        await SaveConfigAsync(config);
    }

    public async Task DeleteProfileAsync(string name)
    {
        var config = await LoadConfigAsync();
        config.Profiles.RemoveAll(p => p.Name == name);

        if (config.SelectedProfile == name)
        {
            config.SelectedProfile = config.Profiles.FirstOrDefault()?.Name;
        }

        await SaveConfigAsync(config);
    }

    public async Task SetSelectedProfileAsync(string name)
    {
        var config = await LoadConfigAsync();

        if (!config.Profiles.Any(p => p.Name == name))
        {
            throw new ArgumentException($"Profile '{name}' not found");
        }

        config.SelectedProfile = name;
        await SaveConfigAsync(config);
    }

    public string DecryptToken(string encryptedToken)
    {
        try
        {
            var encryptedBytes = Convert.FromBase64String(encryptedToken);

            using var aes = Aes.Create();
            aes.Key = _encryptionKey;

            // Extract IV from the beginning of the encrypted data
            var iv = new byte[aes.BlockSize / 8];
            Array.Copy(encryptedBytes, 0, iv, 0, iv.Length);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            using var msDecrypt = new MemoryStream(encryptedBytes, iv.Length, encryptedBytes.Length - iv.Length);
            using var csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read);
            using var srDecrypt = new StreamReader(csDecrypt);

            return srDecrypt.ReadToEnd();
        }
        catch
        {
            // If decryption fails, assume it's already decrypted or invalid
            return encryptedToken;
        }
    }

    private string EncryptToken(string token)
    {
        using var aes = Aes.Create();
        aes.Key = _encryptionKey;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        using var msEncrypt = new MemoryStream();

        // Prepend IV to encrypted data
        msEncrypt.Write(aes.IV, 0, aes.IV.Length);

        using (var csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
        using (var swEncrypt = new StreamWriter(csEncrypt))
        {
            swEncrypt.Write(token);
        }

        return Convert.ToBase64String(msEncrypt.ToArray());
    }

    private async Task<ProfilesConfig> LoadConfigAsync()
    {
        if (_cachedConfig != null)
        {
            return _cachedConfig;
        }

        if (!File.Exists(_profilesPath))
        {
            _cachedConfig = new ProfilesConfig();
            return _cachedConfig;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_profilesPath);
            _cachedConfig = JsonSerializer.Deserialize<ProfilesConfig>(json) ?? new ProfilesConfig();
        }
        catch
        {
            _cachedConfig = new ProfilesConfig();
        }

        return _cachedConfig;
    }

    private async Task SaveConfigAsync(ProfilesConfig config)
    {
        _cachedConfig = config;

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(_profilesPath, json);
    }

    private byte[] GetOrCreateEncryptionKey()
    {
        var keyPath = Path.Combine(FileSystem.AppDataDirectory, ".encryption_key");

        if (File.Exists(keyPath))
        {
            return File.ReadAllBytes(keyPath);
        }

        // Generate new key
        using var aes = Aes.Create();
        aes.GenerateKey();
        var key = aes.Key;

        File.WriteAllBytes(keyPath, key);
        return key;
    }
}
