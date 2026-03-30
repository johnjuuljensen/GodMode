using GodMode.Shared.Models;

namespace GodMode.Server.Services;

public interface IManifestExporter
{
    /// <summary>
    /// Exports the current server state (roots, profiles, MCP servers) as a manifest.
    /// </summary>
    GodModeManifest Export();

    /// <summary>
    /// Serializes a manifest to JSON string.
    /// </summary>
    string Serialize(GodModeManifest manifest);
}
