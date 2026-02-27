using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Reads .godmode-root.json configuration from project root directories.
/// </summary>
public interface IRootConfigReader
{
    /// <summary>
    /// Reads the root configuration from a project root path.
    /// Returns a default config (name + prompt schema) when .godmode-root.json doesn't exist.
    /// Always reads fresh — no caching.
    /// </summary>
    RootConfig ReadConfig(string rootPath);
}
