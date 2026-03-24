using System.Text.Json.Serialization;

namespace GodMode.Shared.Models;

/// <summary>
/// Configuration for a single MCP server instance.
/// Matches Claude Code's native mcpServers format for direct compatibility.
/// Supports stdio (command+args) and HTTP/streamable (url) connection types.
/// </summary>
public record McpServerConfig(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Command = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string[]? Args = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Dictionary<string, string>? Env = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Url = null,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Type = null
);
