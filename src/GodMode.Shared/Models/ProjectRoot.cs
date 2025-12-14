namespace GodMode.Shared.Models;

/// <summary>
/// Represents a named project root directory where projects can be created.
/// </summary>
/// <param name="Name">The display name of the project root.</param>
/// <param name="Path">The absolute path to the project root directory.</param>
public record ProjectRoot(
    string Name,
    string Path
);
