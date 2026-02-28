namespace GodMode.Shared.Models;

/// <summary>
/// Information about a known repository that can be used when creating projects.
/// </summary>
public record RepoInfo(
    string Name,
    string? CloneUrl = null,
    string? Description = null
);
