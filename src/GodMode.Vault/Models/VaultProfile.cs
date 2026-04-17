namespace GodMode.Vault.Models;

/// <summary>
/// Per-user vault setup material for zero-knowledge encryption.
/// The browser generates a random KEK, wraps it under each unlock factor (passkey PRF and/or
/// Argon2id-derived recovery key), and uploads the wrapped copies here.
/// Vault stores these as opaque bytes — it cannot unwrap the KEK and therefore cannot decrypt
/// any secret blobs stored under this user.
/// </summary>
public record VaultProfile
{
    /// <summary>Base64 salt used for Argon2id derivation of the recovery-code key. Typically 16 bytes.</summary>
    public required string Salt { get; init; }

    /// <summary>Base64-encoded WebAuthn credential IDs registered for PRF-based unlock.</summary>
    public required IReadOnlyList<string> PasskeyCredentialIds { get; init; }

    /// <summary>Wrapped-KEK blobs, one per unlock factor. At least one must be present.</summary>
    public required WrappedKek WrappedKek { get; init; }

    /// <summary>Algorithm/format version for forward compatibility. Current: 1.</summary>
    public required int AlgVersion { get; init; }
}

/// <summary>AES-GCM-wrapped copies of the user's KEK, one per unlock factor.</summary>
public record WrappedKek
{
    /// <summary>Base64 blob: KEK wrapped under a key derived from the passkey PRF output.</summary>
    public string? Passkey { get; init; }

    /// <summary>Base64 blob: KEK wrapped under Argon2id(recovery_code, salt).</summary>
    public string? RecoveryCode { get; init; }
}

/// <summary>Response for GET /api/vault/profile. Includes an "initialized" flag for first-visit detection.</summary>
public record VaultProfileResponse
{
    public required bool Initialized { get; init; }
    public string? Salt { get; init; }
    public IReadOnlyList<string>? PasskeyCredentialIds { get; init; }
    public WrappedKek? WrappedKek { get; init; }
    public int? AlgVersion { get; init; }

    public static VaultProfileResponse NotInitialized() => new() { Initialized = false };

    public static VaultProfileResponse From(VaultProfile profile) => new()
    {
        Initialized = true,
        Salt = profile.Salt,
        PasskeyCredentialIds = profile.PasskeyCredentialIds,
        WrappedKek = profile.WrappedKek,
        AlgVersion = profile.AlgVersion
    };
}
