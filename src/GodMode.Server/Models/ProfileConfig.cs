using GodMode.Shared.Models;

namespace GodMode.Server.Models;

/// <summary>
/// Configuration for a server-defined profile.
/// Profiles group roots and carry environment variables (e.g. API keys).
/// </summary>
public class ProfileConfig
{
    /// <summary>
    /// Named project roots: root name -> relative or absolute path.
    /// </summary>
    public Dictionary<string, string> Roots { get; set; } = new();

    /// <summary>
    /// Profile-scoped environment variables merged into all projects in this profile.
    /// Action-level env overrides these on conflict.
    /// </summary>
    public Dictionary<string, string>? Environment { get; set; }

    /// <summary>
    /// Optional description of the profile.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Profile-scoped MCP servers merged into all projects in this profile.
    /// Action-level mcpServers override these on conflict. Null values remove inherited servers.
    /// </summary>
    public Dictionary<string, McpServerConfig?>? McpServers { get; set; }
}
