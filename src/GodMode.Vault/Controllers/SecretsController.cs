using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GodMode.Vault.Models;
using GodMode.Vault.Services;

namespace GodMode.Vault.Controllers;

[ApiController]
[Route("api/secrets")]
[Authorize]
public class SecretsController(FileSecretStore store) : ControllerBase
{
    /// <summary>
    /// Check profile completeness. The caller sends the profile name and required secret names.
    /// Returns which secrets are present/missing/expired.
    /// </summary>
    [HttpPost("check")]
    public async Task<ActionResult<ProfileCheckResult>> CheckProfile(
        [FromBody] ProfileCheckRequest request, CancellationToken ct)
    {
        if (!ValidateNames(request.Profile, request.Secrets, out var error))
            return BadRequest(error);

        var userSub = UserIdentity.GetUserSub(User);
        var result = await store.CheckProfileAsync(userSub, request.Profile, request.Secrets, ct);
        return Ok(result);
    }

    /// <summary>
    /// Fetch all secrets for a profile. The caller sends the profile name and required secret names.
    /// Returns 200 with base64-encoded values if all present, 409 with missing list if not.
    /// </summary>
    [HttpPost("fetch")]
    public async Task<IActionResult> FetchProfileSecrets(
        [FromBody] ProfileCheckRequest request, CancellationToken ct)
    {
        if (!ValidateNames(request.Profile, request.Secrets, out var error))
            return BadRequest(error);

        var userSub = UserIdentity.GetUserSub(User);
        var check = await store.CheckProfileAsync(userSub, request.Profile, request.Secrets, ct);

        if (!check.Ready)
            return Conflict(check);

        var secrets = await store.GetProfileSecretsAsync(userSub, request.Profile, request.Secrets, ct);
        var response = secrets.ToDictionary(kv => kv.Key, kv => Convert.ToBase64String(kv.Value));
        return Ok(response);
    }

    /// <summary>Store a secret value for the authenticated user.</summary>
    [HttpPut("{profile}/{secretName}")]
    public async Task<IActionResult> Store(
        string profile, string secretName,
        [FromBody] StoreSecretRequest request, CancellationToken ct)
    {
        if (!ValidateNames(profile, secretName, out var error))
            return BadRequest(error);

        if (string.IsNullOrWhiteSpace(request.ValueBase64))
            return BadRequest("ValueBase64 is required and must not be blank.");

        byte[] value;
        try { value = Convert.FromBase64String(request.ValueBase64); }
        catch (FormatException) { return BadRequest("ValueBase64 is not valid base64."); }

        if (value.Length == 0)
            return BadRequest("Secret value must not be empty.");

        TimeSpan? ttl;
        try { ttl = TtlParser.Parse(request.Ttl); }
        catch (FormatException ex) { return BadRequest(ex.Message); }

        await store.StoreAsync(userSub: UserIdentity.GetUserSub(User), profile, secretName, value, ttl, ct);
        return NoContent();
    }

    /// <summary>Store a secret from raw binary body (Content-Type: application/octet-stream).</summary>
    [HttpPut("{profile}/{secretName}/raw")]
    public async Task<IActionResult> StoreRaw(
        string profile, string secretName,
        [FromQuery] string? ttl,
        CancellationToken ct)
    {
        if (!ValidateNames(profile, secretName, out var error))
            return BadRequest(error);

        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        var value = ms.ToArray();

        if (value.Length == 0)
            return BadRequest("Request body must not be empty.");

        TimeSpan? parsedTtl;
        try { parsedTtl = TtlParser.Parse(ttl); }
        catch (FormatException ex) { return BadRequest(ex.Message); }

        await store.StoreAsync(UserIdentity.GetUserSub(User), profile, secretName, value, parsedTtl, ct);
        return NoContent();
    }

    /// <summary>Get a single secret as raw binary.</summary>
    [HttpGet("{profile}/{secretName}")]
    public async Task<IActionResult> Get(string profile, string secretName, CancellationToken ct)
    {
        if (!ValidateNames(profile, secretName, out var error))
            return BadRequest(error);

        var value = await store.GetAsync(UserIdentity.GetUserSub(User), profile, secretName, ct);
        if (value == null) return NotFound();
        return File(value, "application/octet-stream");
    }

    /// <summary>Get metadata for a single secret.</summary>
    [HttpGet("{profile}/{secretName}/meta")]
    public async Task<IActionResult> GetMeta(string profile, string secretName, CancellationToken ct)
    {
        if (!ValidateNames(profile, secretName, out var error))
            return BadRequest(error);

        var meta = await store.GetMetadataAsync(UserIdentity.GetUserSub(User), profile, secretName, ct);
        if (meta == null) return NotFound();
        return Ok(meta);
    }

    /// <summary>Delete a secret.</summary>
    [HttpDelete("{profile}/{secretName}")]
    public IActionResult Delete(string profile, string secretName)
    {
        if (!ValidateNames(profile, secretName, out var error))
            return BadRequest(error);

        store.Delete(UserIdentity.GetUserSub(User), profile, secretName);
        return NoContent();
    }

    /// <summary>List profiles that have any stored secrets.</summary>
    [HttpGet("profiles")]
    public IActionResult ListProfiles()
    {
        var profiles = store.ListProfiles(UserIdentity.GetUserSub(User));
        return Ok(profiles);
    }

    /// <summary>List secret names stored under a profile.</summary>
    [HttpGet("{profile}")]
    public IActionResult ListSecrets(string profile)
    {
        if (!ValidateNames(profile, out var error))
            return BadRequest(error);

        var secrets = store.ListSecrets(UserIdentity.GetUserSub(User), profile);
        return Ok(secrets);
    }

    private static bool ValidateNames(string profile, string secretName, out string error)
    {
        error = "";
        try
        {
            FileSecretStore.ValidateName(profile);
            FileSecretStore.ValidateName(secretName);
            return true;
        }
        catch (ArgumentException ex) { error = ex.Message; return false; }
    }

    private static bool ValidateNames(string profile, IReadOnlyList<string> secretNames, out string error)
    {
        error = "";
        try
        {
            FileSecretStore.ValidateName(profile);
            foreach (var name in secretNames) FileSecretStore.ValidateName(name);
            return true;
        }
        catch (ArgumentException ex) { error = ex.Message; return false; }
    }

    private static bool ValidateNames(string name, out string error)
    {
        error = "";
        try { FileSecretStore.ValidateName(name); return true; }
        catch (ArgumentException ex) { error = ex.Message; return false; }
    }
}
