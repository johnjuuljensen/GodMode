namespace GodMode.Vault.Models;

public record ProfileCheckResult(
    string Profile,
    bool Ready,
    IReadOnlyList<SecretStatus> Secrets);
