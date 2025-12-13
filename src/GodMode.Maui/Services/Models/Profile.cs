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
/// Represents an account configuration within a profile
/// </summary>
public class Account
{
    public string Type { get; set; } = string.Empty; // "github", "local"
    public string? Username { get; set; }
    public string? Token { get; set; } // Encrypted for GitHub
    public string? Path { get; set; } // For local accounts
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
