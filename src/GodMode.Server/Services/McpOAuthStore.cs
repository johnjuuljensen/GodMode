using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace GodMode.Server.Services;

/// <summary>
/// Stores MCP OAuth tokens per profile per connector.
/// Tokens are encrypted at rest using Data Protection.
/// File: .profiles/{profile}/mcp-tokens/{connectorId}.json
/// </summary>
public class McpOAuthStore
{
    private readonly IDataProtector _protector;
    private readonly string _projectRootsDir;
    private readonly ILogger<McpOAuthStore> _logger;

    // In-memory cache: "profile/connectorId" → tokens
    private readonly ConcurrentDictionary<string, McpOAuthTokens> _cache = new();

    public McpOAuthStore(IDataProtectionProvider dpProvider, IConfiguration config, ILogger<McpOAuthStore> logger)
    {
        _protector = dpProvider.CreateProtector("McpOAuth");
        _projectRootsDir = Path.GetFullPath(config["ProjectRootsDir"] ?? "roots");
        _logger = logger;
        LoadAll();
    }

    public McpOAuthTokens? GetTokens(string profileName, string connectorId)
    {
        _cache.TryGetValue($"{profileName}/{connectorId}", out var tokens);
        return tokens;
    }

    public Dictionary<string, McpOAuthTokens> GetAllForProfile(string profileName)
    {
        var result = new Dictionary<string, McpOAuthTokens>();
        foreach (var (key, value) in _cache)
        {
            if (key.StartsWith($"{profileName}/", StringComparison.OrdinalIgnoreCase))
                result[key[($"{profileName}/".Length)..]] = value;
        }
        return result;
    }

    public void Store(string profileName, string connectorId, McpOAuthTokens tokens)
    {
        var key = $"{profileName}/{connectorId}";
        _cache[key] = tokens;
        SaveToDisk(profileName, connectorId, tokens);
        _logger.LogInformation("Stored MCP OAuth tokens for {Key}", key);
    }

    public void Delete(string profileName, string connectorId)
    {
        var key = $"{profileName}/{connectorId}";
        _cache.TryRemove(key, out _);
        var path = GetTokenPath(profileName, connectorId);
        if (File.Exists(path)) File.Delete(path);
        _logger.LogInformation("Deleted MCP OAuth tokens for {Key}", key);
    }

    private void LoadAll()
    {
        var profilesDir = Path.Combine(_projectRootsDir, ".profiles");
        if (!Directory.Exists(profilesDir)) return;

        foreach (var profileDir in Directory.GetDirectories(profilesDir))
        {
            var profileName = Path.GetFileName(profileDir);
            var tokensDir = Path.Combine(profileDir, "mcp-tokens");
            if (!Directory.Exists(tokensDir)) continue;

            foreach (var file in Directory.GetFiles(tokensDir, "*.json"))
            {
                var connectorId = Path.GetFileNameWithoutExtension(file);
                try
                {
                    var encrypted = File.ReadAllText(file);
                    var json = _protector.Unprotect(encrypted);
                    var tokens = JsonSerializer.Deserialize<McpOAuthTokens>(json);
                    if (tokens != null)
                        _cache[$"{profileName}/{connectorId}"] = tokens;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load MCP OAuth tokens for {Profile}/{Connector}",
                        profileName, connectorId);
                }
            }
        }
    }

    private void SaveToDisk(string profileName, string connectorId, McpOAuthTokens tokens)
    {
        var dir = Path.Combine(_projectRootsDir, ".profiles", profileName, "mcp-tokens");
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(tokens);
        var encrypted = _protector.Protect(json);
        File.WriteAllText(Path.Combine(dir, $"{connectorId}.json"), encrypted);
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
