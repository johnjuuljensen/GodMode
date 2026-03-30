namespace GodMode.Shared.Models;

/// <summary>
/// Configuration for an MCP (Model Context Protocol) server.
/// Matches the Claude Code MCP config format.
/// </summary>
public record McpServerConfig(
    string Command,
    string[]? Args = null,
    Dictionary<string, string>? Env = null);
