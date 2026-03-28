using System.Text.Json.Serialization;

namespace GodMode.Shared.Models;

/// <summary>
/// A server entry from the Smithery MCP registry search results.
/// </summary>
public record McpRegistryServer(
    string QualifiedName,
    string DisplayName,
    string? Description = null,
    string? IconUrl = null,
    bool Verified = false,
    int UseCount = 0,
    bool Remote = false,
    bool IsDeployed = false,
    string? Homepage = null
);

/// <summary>
/// Paginated search results from the Smithery registry.
/// </summary>
public record McpRegistrySearchResult(
    McpRegistryServer[] Servers,
    McpRegistryPagination Pagination
);

public record McpRegistryPagination(
    int CurrentPage,
    int PageSize,
    int TotalPages,
    int TotalCount
);

/// <summary>
/// Full detail for a single MCP server from the Smithery registry.
/// Includes connection config, available tools, and auth requirements.
/// </summary>
public record McpServerDetail(
    string QualifiedName,
    string DisplayName,
    string? Description = null,
    string? IconUrl = null,
    bool Remote = false,
    string? DeploymentUrl = null,
    McpServerConnection[]? Connections = null,
    McpServerTool[]? Tools = null
);

/// <summary>
/// A connection method for an MCP server (stdio or http).
/// configSchema is a JSON Schema describing required config fields (API keys, tokens, etc).
/// </summary>
public record McpServerConnection(
    string Type,
    string? DeploymentUrl = null,

    [property: JsonPropertyName("configSchema")]
    System.Text.Json.JsonElement? ConfigSchema = null
);

public record McpServerTool(
    string Name,
    string? Description = null
);
