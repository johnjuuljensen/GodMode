namespace GodMode.Vault.Models;

public record ProfileCheckRequest(string Profile, IReadOnlyList<string> Secrets);
