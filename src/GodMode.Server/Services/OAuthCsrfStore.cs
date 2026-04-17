using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace GodMode.Server.Services;

/// <summary>
/// In-memory CSRF token store for OAuth flows.
/// Tokens expire after 10 minutes and are single-use.
/// </summary>
public class OAuthCsrfStore : IDisposable
{
    public record CsrfEntry(string Provider, string? ProfileId, string Purpose, DateTime CreatedAt);
    public record McpOAuthPendingFlow(
        string ConnectorId, string ProfileName, string CodeVerifier,
        string McpServerUrl, string ClientId, string RedirectUri, string TokenEndpoint, DateTime CreatedAt);

    private readonly ConcurrentDictionary<string, CsrfEntry> _entries = new();
    private readonly ConcurrentDictionary<string, McpOAuthPendingFlow> _mcpPending = new();
    private readonly Timer _cleanupTimer;
    private static readonly TimeSpan Expiry = TimeSpan.FromMinutes(10);

    public OAuthCsrfStore()
    {
        _cleanupTimer = new Timer(_ => Cleanup(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
    }

    /// <summary>
    /// Generate a CSRF token for an OAuth flow.
    /// </summary>
    /// <param name="provider">OAuth provider (google, microsoft, atlassian)</param>
    /// <param name="profileId">Profile ID (null for login flows)</param>
    /// <param name="purpose">"login" or "connector"</param>
    public string Generate(string provider, string? profileId, string purpose)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _entries[token] = new CsrfEntry(provider, profileId, purpose, DateTime.UtcNow);
        return token;
    }

    /// <summary>
    /// Validate and consume a CSRF token (one-time use).
    /// Returns null if token is invalid, expired, or already consumed.
    /// </summary>
    public CsrfEntry? Validate(string token)
    {
        if (!_entries.TryRemove(token, out var entry))
            return null;

        if (DateTime.UtcNow - entry.CreatedAt > Expiry)
            return null;

        return entry;
    }

    /// <summary>Store a pending MCP OAuth flow (expires with the same timer).</summary>
    public void StoreMcpPending(string state, McpOAuthPendingFlow flow) => _mcpPending[state] = flow;

    /// <summary>Consume a pending MCP OAuth flow (one-time use).</summary>
    public McpOAuthPendingFlow? ConsumeMcpPending(string state)
    {
        if (!_mcpPending.TryRemove(state, out var flow)) return null;
        return DateTime.UtcNow - flow.CreatedAt > Expiry ? null : flow;
    }

    private void Cleanup()
    {
        var cutoff = DateTime.UtcNow - Expiry;
        foreach (var kvp in _entries)
        {
            if (kvp.Value.CreatedAt < cutoff)
                _entries.TryRemove(kvp.Key, out _);
        }
        foreach (var kvp in _mcpPending)
        {
            if (kvp.Value.CreatedAt < cutoff)
                _mcpPending.TryRemove(kvp.Key, out _);
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        GC.SuppressFinalize(this);
    }
}
