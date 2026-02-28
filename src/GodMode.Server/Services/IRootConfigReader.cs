using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Reads .godmode-root/config.json configuration from project root directories.
/// Falls back to legacy .godmode-root.json for backward compatibility.
/// </summary>
public interface IRootConfigReader
{
    /// <summary>
    /// Reads the root configuration from a project root path.
    /// Returns a default config (name + prompt schema) when no config file exists.
    /// Always reads fresh — no caching.
    /// </summary>
    RootConfig ReadConfig(string rootPath);
}
