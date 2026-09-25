using System.Collections.Concurrent;
using System.Text.Json;

namespace GodMode.Server.Services;

/// <summary>
/// Reads profile configuration from files on disk under {ProjectRootsDir}/.profiles/.
/// Each profile is a directory containing profile.json and env.json. Profiles are maintained by
/// hand on the host: adding a profile = adding a directory. The server never writes them.
/// </summary>
public class ProfileFileManager
{
    private readonly string _profilesDir;
    private readonly ILogger<ProfileFileManager> _logger;

    /// <summary>The profiles whose mcp folder has been logged as ignored: it is said once, not on every read.</summary>
    private readonly ConcurrentDictionary<string, byte> _ignoredMcpLogged = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
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

    /// <summary>
    /// Reads all profiles from .profiles/ directory.
    /// Returns a dictionary of profile name → (description, environment).
    /// </summary>
    public Dictionary<string, ProfileData> ReadAllProfiles()
    {
        var result = new Dictionary<string, ProfileData>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_profilesDir)) return result;

        foreach (var dir in Directory.GetDirectories(_profilesDir))
        {
            var name = Path.GetFileName(dir)!;
            WarnIfMcpFolder(name, dir);
            result[name] = ReadProfile(dir);
        }

        return result;
    }

    /// <summary>
    /// Reads a single profile from its directory.
    /// </summary>
    private static ProfileData ReadProfile(string profileDir)
    {
        string? description = null;
        Dictionary<string, string>? environment = null;

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

        return new ProfileData(description, environment);
    }

    /// <summary>
    /// A profile's mcp folder is not read: GodMode gives a session no MCP server but its own. It is
    /// logged as ignored, once per profile, as a warning.
    /// </summary>
    private void WarnIfMcpFolder(string profileName, string profileDir)
    {
        var mcpDir = Path.Combine(profileDir, "mcp");
        if (Directory.Exists(mcpDir) && _ignoredMcpLogged.TryAdd(mcpDir, 0))
            _logger.LogWarning("Profile '{ProfileName}' has an mcp folder ({Path}), which GodMode ignores: {Hint}",
                profileName, mcpDir, RootConfigReader.McpServersHint);
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
    Dictionary<string, string>? Environment);
