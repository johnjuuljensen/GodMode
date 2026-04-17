using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Manages profile configuration as files on disk under {ProjectRootsDir}/.profiles/.
/// Each profile is a directory containing profile.json, env.json, and mcp/*.json files.
/// File-based: adding a profile = adding a directory. No shared file editing.
/// </summary>
public class ProfileFileManager
{
    private readonly string _profilesDir;
    private readonly ILogger<ProfileFileManager> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ProfileFileManager(IConfiguration configuration, ILogger<ProfileFileManager> logger)
    {
        var projectRootsDir = configuration["ProjectRootsDir"] ?? "roots";
        _profilesDir = Path.Combine(Path.GetFullPath(projectRootsDir), ".profiles");
        _logger = logger;
    }

    /// <summary>
    /// Full path to the .profiles/ directory.
    /// </summary>
    public string ProfilesDir => _profilesDir;

    public void CreateProfile(string name, string? description)
    {
        var profileDir = GetProfileDir(name);
        if (Directory.Exists(profileDir))
            throw new InvalidOperationException($"Profile '{name}' already exists.");

        Directory.CreateDirectory(profileDir);
        WriteProfileJson(profileDir, description);

        _logger.LogInformation("Created profile '{ProfileName}' at {Path}", name, profileDir);
    }

    public void DeleteProfile(string name)
    {
        var profileDir = GetProfileDir(name);
        if (Directory.Exists(profileDir))
            Directory.Delete(profileDir, recursive: true);

        _logger.LogInformation("Deleted profile '{ProfileName}'", name);
    }

    public void UpdateProfileDescription(string name, string? description)
    {
        var profileDir = GetProfileDir(name);
        if (!Directory.Exists(profileDir))
            throw new KeyNotFoundException($"Profile '{name}' not found.");

        WriteProfileJson(profileDir, description);
        _logger.LogInformation("Updated description for profile '{ProfileName}'", name);
    }

    public void AddMcpServerToProfile(string profileName, string serverName, McpServerConfig config)
    {
        var profileDir = GetProfileDir(profileName);
        // Auto-create profile directory for auto-discovered profiles
        Directory.CreateDirectory(profileDir);

        var mcpDir = Path.Combine(profileDir, "mcp");
        Directory.CreateDirectory(mcpDir);

        var serverPath = Path.Combine(mcpDir, $"{serverName}.json");
        File.WriteAllText(serverPath, JsonSerializer.Serialize(config, JsonOptions));

        _logger.LogInformation("Added MCP server '{ServerName}' to profile '{ProfileName}'", serverName, profileName);
    }

    public void RemoveMcpServerFromProfile(string profileName, string serverName)
    {
        var profileDir = GetProfileDir(profileName);
        var serverPath = Path.Combine(profileDir, "mcp", $"{serverName}.json");
        if (!File.Exists(serverPath))
        {
            _logger.LogWarning("MCP server '{ServerName}' not found in profile '{ProfileName}' .profiles/ directory (may be legacy or auto-discovered)", serverName, profileName);
            return;
        }

        File.Delete(serverPath);

        // Clean up empty mcp directory
        var mcpDir = Path.Combine(profileDir, "mcp");
        if (Directory.Exists(mcpDir) && !Directory.EnumerateFileSystemEntries(mcpDir).Any())
            Directory.Delete(mcpDir);

        _logger.LogInformation("Removed MCP server '{ServerName}' from profile '{ProfileName}'", serverName, profileName);
    }

    public void SetProfileEnvironment(string profileName, Dictionary<string, string>? env)
    {
        var profileDir = GetProfileDir(profileName);
        Directory.CreateDirectory(profileDir);

        var envPath = Path.Combine(profileDir, "env.json");
        if (env is { Count: > 0 })
            File.WriteAllText(envPath, JsonSerializer.Serialize(env, JsonOptions));
        else if (File.Exists(envPath))
            File.Delete(envPath);

        _logger.LogInformation("Updated environment for profile '{ProfileName}'", profileName);
    }

    /// <summary>
    /// Reads all profiles from .profiles/ directory.
    /// Returns a dictionary of profile name → (description, environment, mcpServers).
    /// </summary>
    public Dictionary<string, ProfileData> ReadAllProfiles()
    {
        var result = new Dictionary<string, ProfileData>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_profilesDir)) return result;

        foreach (var dir in Directory.GetDirectories(_profilesDir))
        {
            var name = Path.GetFileName(dir)!;
            result[name] = ReadProfile(dir);
        }

        return result;
    }

    /// <summary>
    /// Reads a single profile from its directory.
    /// </summary>
    public static ProfileData ReadProfile(string profileDir)
    {
        string? description = null;
        Dictionary<string, string>? environment = null;
        Dictionary<string, McpServerConfig>? mcpServers = null;

        // Read profile.json
        var profileJsonPath = Path.Combine(profileDir, "profile.json");
        if (File.Exists(profileJsonPath))
        {
            var profileJson = JsonSerializer.Deserialize<ProfileMetadata>(
                File.ReadAllText(profileJsonPath), JsonOptions);
            description = profileJson?.Description;
        }

        // Read env.json
        var envPath = Path.Combine(profileDir, "env.json");
        if (File.Exists(envPath))
        {
            environment = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(envPath), JsonOptions);
        }

        // Read mcp/*.json
        var mcpDir = Path.Combine(profileDir, "mcp");
        if (Directory.Exists(mcpDir))
        {
            mcpServers = new Dictionary<string, McpServerConfig>();
            foreach (var mcpFile in Directory.GetFiles(mcpDir, "*.json"))
            {
                var serverName = Path.GetFileNameWithoutExtension(mcpFile)!;
                var config = JsonSerializer.Deserialize<McpServerConfig>(
                    File.ReadAllText(mcpFile), JsonOptions);
                if (config != null)
                    mcpServers[serverName] = config;
            }
            if (mcpServers.Count == 0) mcpServers = null;
        }

        return new ProfileData(description, environment, mcpServers);
    }

    private string GetProfileDir(string name) => Path.Combine(_profilesDir, name);

    private static void WriteProfileJson(string profileDir, string? description)
    {
        var profileJsonPath = Path.Combine(profileDir, "profile.json");
        if (description != null)
        {
            var metadata = new ProfileMetadata(description);
            File.WriteAllText(profileJsonPath, JsonSerializer.Serialize(metadata, JsonOptions));
        }
        else if (File.Exists(profileJsonPath))
        {
            File.Delete(profileJsonPath);
        }
    }

    /// <summary>
    /// Migrates legacy profiles from appsettings.json Profiles section to .profiles/ directory.
    /// Only runs if .profiles/ does not yet exist and Profiles section has data.
    /// </summary>
    public void MigrateFromAppSettings(IConfiguration configuration)
    {
        // Skip if .profiles/ already has content (migration already done)
        if (Directory.Exists(_profilesDir) && Directory.GetDirectories(_profilesDir).Length > 0) return;

        var profiles = configuration.GetSection("Profiles")
            .Get<Dictionary<string, Models.ProfileConfig>>();
        if (profiles is not { Count: > 0 }) return;

        _logger.LogInformation("Migrating {Count} profiles from appsettings.json to .profiles/ directory", profiles.Count);

        foreach (var (name, config) in profiles)
        {
            try
            {
                CreateProfile(name, config.Description);

                if (config.Environment is { Count: > 0 })
                    SetProfileEnvironment(name, config.Environment);

                if (config.McpServers is { Count: > 0 })
                {
                    foreach (var (serverName, serverConfig) in config.McpServers)
                        AddMcpServerToProfile(name, serverName, serverConfig);
                }

                _logger.LogInformation("Migrated profile '{ProfileName}' to .profiles/", name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to migrate profile '{ProfileName}'", name);
            }
        }
    }

    /// <summary>
    /// Profile metadata from profile.json (description only).
    /// </summary>
    private record ProfileMetadata(string? Description = null);
}

/// <summary>
/// In-memory representation of a profile read from .profiles/ directory.
/// </summary>
public record ProfileData(
    string? Description,
    Dictionary<string, string>? Environment,
    Dictionary<string, McpServerConfig>? McpServers);
