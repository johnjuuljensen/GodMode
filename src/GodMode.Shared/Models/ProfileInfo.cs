namespace GodMode.Shared.Models;

/// <summary>
/// Information about a server-defined profile.
/// </summary>
/// <param name="Name">The profile name.</param>
/// <param name="Description">Optional description of the profile.</param>
public record ProfileInfo(string Name, string? Description = null);
