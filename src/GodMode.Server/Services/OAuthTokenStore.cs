using System.Text.Json;
using GodMode.Shared;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.DataProtection;

namespace GodMode.Server.Services;

/// <summary>
/// Encrypted token storage for OAuth providers, per-profile.
/// Tokens are stored at .profiles/{name}/oauth/{provider}.json.enc using ASP.NET Data Protection.
/// </summary>
public class OAuthTokenStore
{
    private readonly string _rootsDir;
    private readonly IDataProtector _protector;
    private readonly ILogger<OAuthTokenStore> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public OAuthTokenStore(IConfiguration config, IDataProtectionProvider dataProtection, ILogger<OAuthTokenStore> logger)
    {
        _rootsDir = Path.GetFullPath(config["ProjectRootsDir"] ?? "roots");
        _protector = dataProtection.CreateProtector("OAuthTokens");
        _logger = logger;
    }

    /// <summary>
    /// Store OAuth tokens for a provider in a profile.
    /// </summary>
    public void StoreTokens(string profileName, string provider, OAuthTokenSet tokens)
    {
        var dir = GetOAuthDir(profileName);
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(tokens, JsonOptions);
        var encrypted = _protector.Protect(json);
        File.WriteAllText(GetTokenPath(profileName, provider), encrypted);

        _logger.LogInformation("Stored OAuth tokens for {Provider} in profile {Profile}", provider, profileName);
    }

    /// <summary>
    /// Load OAuth tokens for a provider from a profile.
    /// Returns null if no tokens are stored or decryption fails.
    /// </summary>
    public OAuthTokenSet? LoadTokens(string profileName, string provider)
    {
        var path = GetTokenPath(profileName, provider);
        if (!File.Exists(path))
            return null;

        try
        {
            var encrypted = File.ReadAllText(path);
            var json = _protector.Unprotect(encrypted);
            return JsonSerializer.Deserialize<OAuthTokenSet>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load OAuth tokens for {Provider} in profile {Profile}", provider, profileName);
            return null;
        }
    }

    /// <summary>
    /// Delete stored tokens for a provider in a profile.
    /// </summary>
    public void DeleteTokens(string profileName, string provider)
    {
        var path = GetTokenPath(profileName, provider);
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogInformation("Deleted OAuth tokens for {Provider} in profile {Profile}", provider, profileName);
        }
    }

    /// <summary>
    /// Get the connection status for all known OAuth providers in a profile.
    /// </summary>
    public Dictionary<string, OAuthProviderStatus> GetProviderStatuses(string profileName)
    {
        var result = new Dictionary<string, OAuthProviderStatus>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in OAuthProviderMapping.SupportedProviders)
        {
            var tokens = LoadTokens(profileName, provider);
            if (tokens != null)
            {
                var expiresAt = DateTimeOffset.FromUnixTimeSeconds(tokens.ExpiresAt).ToString("o");
                result[provider] = new OAuthProviderStatus(Connected: true, ExpiresAt: expiresAt, Email: tokens.Email);
            }
            else
            {
                result[provider] = new OAuthProviderStatus(Connected: false);
            }
        }

        return result;
    }

    /// <summary>
    /// Load all OAuth tokens for a profile (for MCP config injection).
    /// Returns only providers with valid stored tokens.
    /// </summary>
    public Dictionary<string, OAuthTokenSet> LoadAllTokens(string profileName)
    {
        var result = new Dictionary<string, OAuthTokenSet>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in OAuthProviderMapping.SupportedProviders)
        {
            var tokens = LoadTokens(profileName, provider);
            if (tokens != null)
                result[provider] = tokens;
        }

        return result;
    }

    private string GetOAuthDir(string profileName) =>
        Path.Combine(_rootsDir, ".profiles", profileName, "oauth");

    private string GetTokenPath(string profileName, string provider) =>
        Path.Combine(GetOAuthDir(profileName), $"{provider}.json.enc");
}
