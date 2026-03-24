using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Persists runtime profile overrides (McpServers, Environment additions) to ~/.godmode/profiles.json.
/// These are merged on top of appsettings.json ProfileConfig at runtime.
/// </summary>
public class ProfileOverrideStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".godmode", "profile-overrides.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Reads all profile overrides from disk.
    /// </summary>
    public Dictionary<string, ProfileOverride> Load()
    {
        if (!File.Exists(StorePath))
            return new Dictionary<string, ProfileOverride>(StringComparer.OrdinalIgnoreCase);

        var json = File.ReadAllText(StorePath);
        return JsonSerializer.Deserialize<Dictionary<string, ProfileOverride>>(json, JsonOptions)
            ?? new Dictionary<string, ProfileOverride>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Saves all profile overrides to disk.
    /// </summary>
    public void Save(Dictionary<string, ProfileOverride> overrides)
    {
        var dir = Path.GetDirectoryName(StorePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(overrides, JsonOptions));
    }

    /// <summary>
    /// Adds or updates an MCP server in a profile's overrides.
    /// </summary>
    public void SetMcpServer(string profileName, string serverName, McpServerConfig? config)
    {
        var overrides = Load();
        if (!overrides.TryGetValue(profileName, out var profile))
        {
            profile = new ProfileOverride();
            overrides[profileName] = profile;
        }
        profile.McpServers ??= new Dictionary<string, McpServerConfig?>(StringComparer.OrdinalIgnoreCase);
        profile.McpServers[serverName] = config;
        Save(overrides);
    }

    /// <summary>
    /// Removes an MCP server from a profile's overrides.
    /// </summary>
    public void RemoveMcpServer(string profileName, string serverName)
    {
        var overrides = Load();
        if (overrides.TryGetValue(profileName, out var profile) && profile.McpServers != null)
        {
            profile.McpServers.Remove(serverName);
            if (profile.McpServers.Count == 0) profile.McpServers = null;
            if (profile.IsEmpty) overrides.Remove(profileName);
            Save(overrides);
        }
    }

    /// <summary>
    /// Creates a new profile override entry (used to create profiles that don't exist in appsettings).
    /// </summary>
    public void CreateProfile(string profileName, string? description)
    {
        var overrides = Load();
        if (!overrides.ContainsKey(profileName))
        {
            overrides[profileName] = new ProfileOverride { Description = description };
            Save(overrides);
        }
    }

    /// <summary>
    /// Updates a profile's description.
    /// </summary>
    public void UpdateProfileDescription(string profileName, string? description)
    {
        var overrides = Load();
        if (!overrides.TryGetValue(profileName, out var profile))
        {
            profile = new ProfileOverride();
            overrides[profileName] = profile;
        }
        profile.Description = description;
        Save(overrides);
    }
}

public class ProfileOverride
{
    public Dictionary<string, McpServerConfig?>? McpServers { get; set; }
    public string? Description { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => (McpServers is null or { Count: 0 }) && Description is null;
}
