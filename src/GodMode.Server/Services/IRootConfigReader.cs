using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Reads root configuration from project root directories using multi-file discovery.
/// Scans .godmode-root/ for config.json (base) and config.*.json (per-action overlays).
/// Action names are derived from filenames: config.freeform.json → "freeform".
/// Schema files are discovered by convention: {actionName}/schema.json.
/// Returns a resolved RootConfig with all merging already applied.
/// </summary>
public interface IRootConfigReader
{
    /// <summary>
    /// Reads and resolves the root configuration from a project root path.
    /// Returns a default config (single "Create" action with name + prompt schema)
    /// when no config file exists.
    /// Always reads fresh — no caching.
    /// </summary>
    RootConfig ReadConfig(string rootPath);

    /// <summary>
    /// As <see cref="ReadConfig"/>, but a config file that exists and cannot be read or parsed
    /// throws instead of reading as the default config. A launch uses it: the default config
    /// lacks the action's environment and arguments.
    /// </summary>
    RootConfig ReadConfigStrict(string rootPath);
}
