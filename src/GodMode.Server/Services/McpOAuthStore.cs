using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace GodMode.Server.Services;

/// <summary>
/// Stores MCP OAuth tokens per profile per connector.
/// Tokens are encrypted at rest using Data Protection.
/// File: .profiles/{profile}/mcp-tokens/{connectorId}.json
/// Reads from disk on every call (no in-memory cache) per Section 5.1.
/// </summary>
public class McpOAuthStore
{
    private readonly IDataProtector _protector;
    private readonly string _projectRootsDir;
    private readonly ILogger<McpOAuthStore> _logger;

    public McpOAuthStore(IDataProtectionProvider dpProvider, IConfiguration config, ILogger<McpOAuthStore> logger)
    {
        _protector = dpProvider.CreateProtector("McpOAuth");
        _projectRootsDir = Path.GetFullPath(config["ProjectRootsDir"] ?? "roots");
        _logger = logger;
    }

    public McpOAuthTokens? GetTokens(string profileName, string connectorId)
    {
        var path = GetTokenPath(profileName, connectorId);
        if (!File.Exists(path)) return null;
        try
        {
            var encrypted = File.ReadAllText(path);
            var json = _protector.Unprotect(encrypted);
            return JsonSerializer.Deserialize<McpOAuthTokens>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load MCP OAuth tokens for {Profile}/{Connector}", profileName, connectorId);
            return null;
        }
    }

    public Dictionary<string, McpOAuthTokens> GetAllForProfile(string profileName)
    {
        var result = new Dictionary<string, McpOAuthTokens>();
        var dir = Path.Combine(_projectRootsDir, ".profiles", profileName, "mcp-tokens");
        if (!Directory.Exists(dir)) return result;

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var connectorId = Path.GetFileNameWithoutExtension(file);
            try
            {
                var encrypted = File.ReadAllText(file);
                var json = _protector.Unprotect(encrypted);
                var tokens = JsonSerializer.Deserialize<McpOAuthTokens>(json);
                if (tokens != null) result[connectorId] = tokens;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load MCP OAuth tokens for {Profile}/{Connector}", profileName, connectorId);
            }
        }
        return result;
    }

    public void Store(string profileName, string connectorId, McpOAuthTokens tokens)
    {
        var dir = Path.Combine(_projectRootsDir, ".profiles", profileName, "mcp-tokens");
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(tokens);
        var encrypted = _protector.Protect(json);
        File.WriteAllText(Path.Combine(dir, $"{connectorId}.json"), encrypted);
        _logger.LogInformation("Stored MCP OAuth tokens for {Profile}/{Connector}", profileName, connectorId);
    }

    public void Delete(string profileName, string connectorId)
    {
        var path = GetTokenPath(profileName, connectorId);
        if (File.Exists(path)) File.Delete(path);
        _logger.LogInformation("Deleted MCP OAuth tokens for {Profile}/{Connector}", profileName, connectorId);
    }

    private string GetTokenPath(string profileName, string connectorId) =>
        Path.Combine(_projectRootsDir, ".profiles", profileName, "mcp-tokens", $"{connectorId}.json");
}

public record McpOAuthTokens(
    string AccessToken,
    string? RefreshToken,
    string? ClientId,
    string McpServerUrl,
    long? ExpiresAt,
    string? Email);
