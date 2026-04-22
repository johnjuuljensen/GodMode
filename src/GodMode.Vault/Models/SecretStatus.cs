namespace GodMode.Vault.Models;

public record SecretStatus(string Name, bool Exists, bool Expired, DateTimeOffset? ExpiresAt);
