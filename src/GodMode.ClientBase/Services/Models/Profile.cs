namespace GodMode.ClientBase.Services.Models;

/// <summary>
/// Represents a user profile containing account configurations
/// </summary>
public class Profile
{
    public string Name { get; set; } = string.Empty;
    public List<Account> Accounts { get; set; } = new();

    /// <summary>
    /// Checks whether an account with the same identity already exists in this profile.
    /// For local servers, identity is the normalized URL (case-insensitive, trailing slash stripped).
    /// For GitHub accounts, identity is the username (case-insensitive).
    /// </summary>
    /// <param name="account">The account to check against existing accounts.</param>
    /// <param name="excludeIndex">Optional index to exclude (used when editing an existing account).</param>
    public bool HasDuplicateAccount(Account account, int? excludeIndex = null)
    {
        for (var i = 0; i < Accounts.Count; i++)
        {
            if (i == excludeIndex) continue;
            var existing = Accounts[i];
            if (!string.Equals(existing.Type, account.Type, StringComparison.OrdinalIgnoreCase)) continue;

            if (account.Type == "local" &&
                NormalizeUrl(existing.Path) == NormalizeUrl(account.Path))
                return true;

            if (account.Type == "github" &&
                string.Equals(existing.Username, account.Username, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? NormalizeUrl(string? url) =>
        url?.TrimEnd('/').ToLowerInvariant();
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
