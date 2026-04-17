using GodMode.Shared.Models;

namespace GodMode.Server.Services;

public interface IManifestParser
{
    /// <summary>
    /// Parses a manifest from JSON string content.
    /// </summary>
    GodModeManifest Parse(string content);

    /// <summary>
    /// Parses a manifest from a file path.
    /// </summary>
    GodModeManifest ParseFile(string filePath);

    /// <summary>
    /// Validates a manifest and returns errors, or null if valid.
    /// </summary>
    string? Validate(GodModeManifest manifest);
}
