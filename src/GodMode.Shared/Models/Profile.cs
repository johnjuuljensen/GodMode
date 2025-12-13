namespace GodMode.Shared.Models;

/// <summary>
/// Represents a user profile with associated accounts.
/// </summary>
/// <param name="Name">Profile name.</param>
/// <param name="Accounts">List of accounts associated with this profile.</param>
public record Profile(
    string Name,
    List<Account> Accounts
);

/// <summary>
/// Base class for account types.
/// </summary>
/// <param name="Type">The type of account (e.g., "github", "local").</param>
public abstract record Account(string Type);

/// <summary>
/// Represents a GitHub account.
/// </summary>
/// <param name="Username">GitHub username.</param>
/// <param name="EncryptedToken">Encrypted access token.</param>
public record GitHubAccount(string Username, string EncryptedToken) : Account("github");

/// <summary>
/// Represents a local filesystem account.
/// </summary>
/// <param name="Path">Local filesystem path.</param>
public record LocalAccount(string Path) : Account("local");
