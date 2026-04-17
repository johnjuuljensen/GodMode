namespace GodMode.Shared.Models;

/// <summary>
/// Configuration for an MCP (Model Context Protocol) server.
/// Matches the Claude Code MCP config format.
/// Stdio servers use Command/Args/Env; SSE servers use Url/Headers.
/// </summary>
public record McpServerConfig(
    string? Command = null,
    string[]? Args = null,
    Dictionary<string, string>? Env = null,
    string? Url = null,
    Dictionary<string, string>? Headers = null);
