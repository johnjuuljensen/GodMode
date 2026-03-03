namespace GodMode.ClientBase.Services;

/// <summary>
/// Protects and unprotects tokens for secure storage.
/// Uses OS-level protection (DPAPI on Windows) where available.
/// </summary>
public interface ITokenProtector
{
    /// <summary>
    /// Protects a plaintext token for persistent storage.
    /// </summary>
    string Protect(string token);

    /// <summary>
    /// Recovers the original token from its stored (protected) form.
    /// </summary>
    string Unprotect(string storedToken);
}
