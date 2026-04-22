using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GodMode.Vault.Models;
using GodMode.Vault.Services;

namespace GodMode.Vault.Controllers;

/// <summary>
/// Vault profile endpoints — per-user setup material for zero-knowledge client-side encryption.
/// Vault stores these blobs opaquely; it cannot derive or use the KEK they wrap.
/// </summary>
[ApiController]
[Route("api/vault")]
[Authorize]
public class VaultController(VaultProfileStore store) : ControllerBase
{
    private const int CurrentAlgVersion = 1;
    private const int MinSaltBytes = 8;
    private const int MaxSaltBytes = 64;
    private const int MaxCredentialIds = 16;
    private const int MaxBlobBytes = 512;

    /// <summary>Returns the authenticated user's vault profile, or {initialized:false} if unset.</summary>
    [HttpGet("profile")]
    public async Task<ActionResult<VaultProfileResponse>> GetProfile(CancellationToken ct)
    {
        var userSub = UserIdentity.GetUserSub(User);
        var profile = await store.GetAsync(userSub, ct);
        return profile == null
            ? Ok(VaultProfileResponse.NotInitialized())
            : Ok(VaultProfileResponse.From(profile));
    }

    /// <summary>Stores (or replaces) the authenticated user's vault profile.</summary>
    [HttpPut("profile")]
    public async Task<IActionResult> PutProfile([FromBody] VaultProfile profile, CancellationToken ct)
    {
        if (!Validate(profile, out var error))
            return BadRequest(error);

        await store.StoreAsync(UserIdentity.GetUserSub(User), profile, ct);
        return NoContent();
    }

    private static bool Validate(VaultProfile profile, out string error)
    {
        error = "";

        if (profile.AlgVersion != CurrentAlgVersion)
        {
            error = $"Unsupported algVersion {profile.AlgVersion}. Expected {CurrentAlgVersion}.";
            return false;
        }

        if (!TryDecodeBase64(profile.Salt, MinSaltBytes, MaxSaltBytes, out var saltError))
        {
            error = $"Salt: {saltError}";
            return false;
        }

        if (profile.PasskeyCredentialIds.Count > MaxCredentialIds)
        {
            error = $"PasskeyCredentialIds exceeds maximum of {MaxCredentialIds}.";
            return false;
        }

        foreach (var credId in profile.PasskeyCredentialIds)
        {
            if (!TryDecodeBase64(credId, minBytes: 1, MaxBlobBytes, out var credError))
            {
                error = $"PasskeyCredentialIds entry: {credError}";
                return false;
            }
        }

        var passkeyOk = profile.WrappedKek.Passkey == null ||
            TryDecodeBase64(profile.WrappedKek.Passkey, minBytes: 1, MaxBlobBytes, out _);
        var recoveryOk = profile.WrappedKek.RecoveryCode == null ||
            TryDecodeBase64(profile.WrappedKek.RecoveryCode, minBytes: 1, MaxBlobBytes, out _);

        if (!passkeyOk)
        {
            error = "WrappedKek.Passkey is not valid base64 or exceeds size limit.";
            return false;
        }
        if (!recoveryOk)
        {
            error = "WrappedKek.RecoveryCode is not valid base64 or exceeds size limit.";
            return false;
        }

        if (profile.WrappedKek.Passkey == null && profile.WrappedKek.RecoveryCode == null)
        {
            error = "WrappedKek must contain at least one of Passkey or RecoveryCode.";
            return false;
        }

        // If a passkey-wrapped KEK is present, at least one credential ID must be registered.
        if (profile.WrappedKek.Passkey != null && profile.PasskeyCredentialIds.Count == 0)
        {
            error = "WrappedKek.Passkey requires at least one entry in PasskeyCredentialIds.";
            return false;
        }

        return true;
    }

    private static bool TryDecodeBase64(string? value, int minBytes, int maxBytes, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "required and must not be blank";
            return false;
        }
        byte[] decoded;
        try { decoded = Convert.FromBase64String(value); }
        catch (FormatException) { error = "not valid base64"; return false; }

        if (decoded.Length < minBytes) { error = $"must be at least {minBytes} bytes"; return false; }
        if (decoded.Length > maxBytes) { error = $"must be at most {maxBytes} bytes"; return false; }
        return true;
    }
}
