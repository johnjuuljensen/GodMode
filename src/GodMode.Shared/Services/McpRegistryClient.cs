using System.Net.Http.Json;
using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Shared.Services;

/// <summary>
/// Client for the Smithery MCP server registry API.
/// Used by both server (via SignalR hub) and potentially client-side.
/// </summary>
public class McpRegistryClient
{
    private readonly HttpClient _http;
    private const string BaseUrl = "https://registry.smithery.ai";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public McpRegistryClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Search for MCP servers by query string.
    /// </summary>
    public async Task<McpRegistrySearchResult> SearchAsync(
        string query, int pageSize = 20, int page = 1, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/servers?q={Uri.EscapeDataString(query)}&pageSize={pageSize}&page={page}";
        var result = await _http.GetFromJsonAsync<McpRegistrySearchResult>(url, JsonOptions, ct);
        return result ?? new McpRegistrySearchResult([], new McpRegistryPagination(1, pageSize, 0, 0));
    }

    /// <summary>
    /// Get full detail for a specific MCP server by qualified name.
    /// </summary>
    public async Task<McpServerDetail?> GetServerDetailAsync(
        string qualifiedName, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/servers/{Uri.EscapeDataString(qualifiedName)}";
        return await _http.GetFromJsonAsync<McpServerDetail>(url, JsonOptions, ct);
    }
}
