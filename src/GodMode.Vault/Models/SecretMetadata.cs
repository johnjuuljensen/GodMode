namespace GodMode.Vault.Models;

public record SecretMetadata
{
    public required string Name { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public TimeSpan? Ttl { get; init; }

    public bool IsExpired => Ttl.HasValue && DateTimeOffset.UtcNow > CreatedAt + Ttl.Value;
    public DateTimeOffset? ExpiresAt => Ttl.HasValue ? CreatedAt + Ttl.Value : null;
}
