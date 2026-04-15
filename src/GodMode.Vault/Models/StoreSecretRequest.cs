namespace GodMode.Vault.Models;

public record StoreSecretRequest
{
    /// <summary>Base64-encoded secret value (for JSON API). Use multipart or raw body for binary.</summary>
    public string? ValueBase64 { get; init; }

    /// <summary>Optional TTL as a duration string (e.g. "90d", "24h", "30m").</summary>
    public string? Ttl { get; init; }
}
