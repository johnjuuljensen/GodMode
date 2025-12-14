namespace GodMode.Maui.Services.Models;

/// <summary>
/// Represents a user profile containing account configurations
/// </summary>
public class Profile
{
    public string Name { get; set; } = string.Empty;
    public List<Account> Accounts { get; set; } = new();
}

/// <summary>
/// Represents an account/server configuration within a profile.
/// Accounts define how to connect to hosts (servers) that run projects.
/// </summary>
public class Account
{
    /// <summary>
    /// The type of account:
    /// - "github": GitHub account for Codespaces API (managing codespaces).
    ///   Git credentials are assumed to be configured on the codespace.
    /// - "local": Local GodMode.Server connection.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// GitHub username (for github accounts only).
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// GitHub personal access token (encrypted) for Codespaces API (for github accounts only).
    /// This is NOT used for git operations - git credentials are configured on the server.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// Server URL for local accounts (e.g., "http://localhost:31337").
    /// Legacy file paths are converted to default localhost URL.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Optional metadata for the account.
    /// Common keys: "name" (display name for the host).
    /// </summary>
    public Dictionary<string, string>? Metadata { get; set; }
}

/// <summary>
/// Container for all profiles
/// </summary>
public class ProfilesConfig
{
    public List<Profile> Profiles { get; set; } = new();
    public string? SelectedProfile { get; set; }
}
