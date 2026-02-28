using GodMode.ClientBase.Services.Models;
using System.Text.Json;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Manages user profiles and account configurations
/// </summary>
public class ProfileService : IProfileService
{
    private const string ProfilesFileName = "profiles.json";
    private readonly string _profilesPath;
    private readonly EncryptionHelper _encryption;
    private ProfilesConfig? _cachedConfig;

    public ProfileService(string appDataPath, EncryptionHelper encryption)
    {
        _profilesPath = Path.Combine(appDataPath, ProfilesFileName);
        _encryption = encryption;
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
            return config.Profiles.FirstOrDefault(p => p.Name == config.SelectedProfile);

        return config.Profiles.FirstOrDefault();
    }

    public async Task SaveProfileAsync(Profile profile)
    {
        var config = await LoadConfigAsync();

        var existing = config.Profiles.FirstOrDefault(p => p.Name == profile.Name);
        if (existing != null)
            config.Profiles.Remove(existing);

        // Encrypt GitHub tokens before saving
        var profileToSave = new Profile
        {
            Name = profile.Name,
            Servers = profile.Servers.Select(s => new ServerConfig
            {
                Id = s.Id,
                DisplayName = s.DisplayName,
                Type = s.Type,
                Username = s.Username,
                Token = s.Type == "github" && !string.IsNullOrEmpty(s.Token)
                    ? _encryption.Encrypt(s.Token)
                    : s.Token,
                Path = s.Path,
                Metadata = s.Metadata,
                Systems = s.Systems
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
            config.SelectedProfile = config.Profiles.FirstOrDefault()?.Name;

        await SaveConfigAsync(config);
    }

    public async Task SetSelectedProfileAsync(string name)
    {
        var config = await LoadConfigAsync();

        if (!config.Profiles.Any(p => p.Name == name))
            throw new ArgumentException($"Profile '{name}' not found");

        config.SelectedProfile = name;
        await SaveConfigAsync(config);
    }

    public string DecryptToken(string encryptedToken) => _encryption.Decrypt(encryptedToken);

    private async Task<ProfilesConfig> LoadConfigAsync()
    {
        if (_cachedConfig != null)
            return _cachedConfig;

        if (!File.Exists(_profilesPath))
        {
            _cachedConfig = new ProfilesConfig();
            return _cachedConfig;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_profilesPath);
            _cachedConfig = JsonSerializer.Deserialize<ProfilesConfig>(json) ?? new ProfilesConfig();

            // Migration: assign IDs to servers that don't have one
            if (MigrateServerIds(_cachedConfig))
                await SaveConfigAsync(_cachedConfig);
        }
        catch
        {
            _cachedConfig = new ProfilesConfig();
        }

        return _cachedConfig;
    }

    /// <summary>
    /// Migrates old accounts without IDs by assigning stable IDs.
    /// Returns true if any migration was performed.
    /// </summary>
    private static bool MigrateServerIds(ProfilesConfig config)
    {
        var migrated = false;
        foreach (var profile in config.Profiles)
        {
            foreach (var server in profile.Servers)
            {
                if (string.IsNullOrEmpty(server.Id))
                {
                    server.Id = Guid.NewGuid().ToString("N")[..8];
                    migrated = true;
                }

                // Migrate Metadata["name"] → DisplayName
                if (server.DisplayName == null && server.Metadata?.TryGetValue("name", out var name) == true)
                {
                    server.DisplayName = name;
                    migrated = true;
                }
            }
        }
        return migrated;
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
}
