namespace GodMode.ClientBase.Services.Models;

/// <summary>
/// A named credential stored in the credential registry.
/// Values are encrypted at rest using EncryptionHelper.
/// </summary>
public record Credential(string Name, string Type, string EncryptedValue);
