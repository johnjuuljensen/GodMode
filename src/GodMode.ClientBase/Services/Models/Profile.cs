using System.Text.Json.Serialization;

namespace GodMode.ClientBase.Services.Models;

/// <summary>
/// Represents a user profile containing server configurations
/// </summary>
public class Profile
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Server configurations for this profile.
    /// JSON property name "accounts" for backward compatibility with old profiles.json.
    /// </summary>
    [JsonPropertyName("accounts")]
    public List<ServerConfig> Servers { get; set; } = new();

    /// <summary>
    /// Backward-compatible alias for <see cref="Servers"/>.
    /// </summary>
    [JsonIgnore]
    [Obsolete("Use Servers instead")]
    public List<ServerConfig> Accounts => Servers;
}

/// <summary>
/// Represents a server/host configuration within a profile.
/// Evolved from the old Account class — adds stable ID, display name, and system mappings.
/// </summary>
public class ServerConfig
{
    /// <summary>
    /// Stable identifier for this server. Auto-generated on migration if missing.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Human-readable display name for the server.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// The type of server:
    /// - "github": GitHub account for Codespaces API.
    /// - "local": Local GodMode.Server connection.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// GitHub username (for github servers only).
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// GitHub personal access token (encrypted) for Codespaces API (for github servers only).
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// Server URL for local servers (e.g., "http://localhost:31337").
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Optional metadata for the server.
    /// Legacy: "name" key was used for display name (now migrated to DisplayName).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Metadata { get; set; }

    /// <summary>
    /// Maps system names to credential names from the credential registry.
    /// Key: system name (e.g., "anthropic"), Value: credential name.
    /// When a project starts, the client resolves these to env vars for Claude.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Systems { get; set; }
}

/// <summary>
/// Container for all profiles
/// </summary>
public class ProfilesConfig
{
    public List<Profile> Profiles { get; set; } = new();
    public string? SelectedProfile { get; set; }
}

/// <summary>
/// Backward-compatible alias for ServerConfig.
/// </summary>
[Obsolete("Use ServerConfig instead")]
public class Account : ServerConfig { }
