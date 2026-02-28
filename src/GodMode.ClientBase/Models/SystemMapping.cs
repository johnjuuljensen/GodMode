namespace GodMode.ClientBase.Models;

/// <summary>
/// Display model for a system-to-credential mapping with resolution status.
/// Used in the creation wizard's systems review step.
/// </summary>
public record SystemMapping(string SystemName, string CredentialName, bool IsResolved);
