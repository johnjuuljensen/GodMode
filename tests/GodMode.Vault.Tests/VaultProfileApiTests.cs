using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using GodMode.Vault.Models;

namespace GodMode.Vault.Tests;

/// <summary>
/// Tests /api/vault/profile endpoints — zero-knowledge setup material for client-side encryption.
/// Uses a dedicated fixture so these tests don't share state with SecretsApiTests.
/// </summary>
public class VaultProfileApiTests : IClassFixture<VaultFixture>
{
    private readonly HttpClient _client;
    private readonly string _profileFile;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public VaultProfileApiTests(VaultFixture fixture)
    {
        _client = fixture.CreateClient();
        // Tests within a class run sequentially but in undefined order. Reset state per test
        // by deleting the profile file — userSub is fixed by TestAuthHandler so we know the path.
        var userDir = Path.Combine(fixture.StoragePath,
            GodMode.Vault.Services.UserIdentity.SanitizeSubForPath(VaultFixture.TestUserSub));
        _profileFile = Path.Combine(userDir, ".vault-profile.json");
        if (File.Exists(_profileFile)) File.Delete(_profileFile);
    }

    // --- First-visit detection ---

    [Fact]
    public async Task GetProfile_WhenNotInitialized_ReturnsInitializedFalse()
    {
        var response = await _client.GetAsync("/api/vault/profile");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<VaultProfileResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.False(body.Initialized);
        Assert.Null(body.Salt);
        Assert.Null(body.WrappedKek);
    }

    // --- Round-trip ---

    [Fact]
    public async Task PutAndGetProfile_RoundTrips()
    {
        var profile = NewValidProfile(bothFactors: true);

        var put = await _client.PutAsJsonAsync("/api/vault/profile", profile, JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var get = await _client.GetAsync("/api/vault/profile");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var body = await get.Content.ReadFromJsonAsync<VaultProfileResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.True(body.Initialized);
        Assert.Equal(profile.Salt, body.Salt);
        Assert.Equal(profile.AlgVersion, body.AlgVersion);
        Assert.Equal(profile.PasskeyCredentialIds, body.PasskeyCredentialIds);
        Assert.NotNull(body.WrappedKek);
        Assert.Equal(profile.WrappedKek.Passkey, body.WrappedKek!.Passkey);
        Assert.Equal(profile.WrappedKek.RecoveryCode, body.WrappedKek.RecoveryCode);
    }

    [Fact]
    public async Task PutProfile_OverwritesExisting()
    {
        var v1 = NewValidProfile(bothFactors: true);
        var v2 = v1 with { Salt = RandomBase64(16) };

        await _client.PutAsJsonAsync("/api/vault/profile", v1, JsonOptions);
        await _client.PutAsJsonAsync("/api/vault/profile", v2, JsonOptions);

        var body = await (await _client.GetAsync("/api/vault/profile"))
            .Content.ReadFromJsonAsync<VaultProfileResponse>(JsonOptions);
        Assert.Equal(v2.Salt, body!.Salt);
    }

    [Fact]
    public async Task PutProfile_RecoveryOnly_IsAllowed()
    {
        var profile = NewValidProfile(bothFactors: false, includeRecovery: true);

        var put = await _client.PutAsJsonAsync("/api/vault/profile", profile, JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
    }

    [Fact]
    public async Task PutProfile_PasskeyOnly_IsAllowed()
    {
        var profile = NewValidProfile(bothFactors: false, includeRecovery: false);

        var put = await _client.PutAsJsonAsync("/api/vault/profile", profile, JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
    }

    // --- Validation ---

    [Fact]
    public async Task PutProfile_UnsupportedAlgVersion_Returns400()
    {
        var profile = NewValidProfile(bothFactors: true) with { AlgVersion = 99 };
        var put = await _client.PutAsJsonAsync("/api/vault/profile", profile, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task PutProfile_InvalidBase64Salt_Returns400()
    {
        var profile = NewValidProfile(bothFactors: true) with { Salt = "not-valid-base64!!!" };
        var put = await _client.PutAsJsonAsync("/api/vault/profile", profile, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task PutProfile_SaltTooShort_Returns400()
    {
        var profile = NewValidProfile(bothFactors: true) with { Salt = RandomBase64(4) };
        var put = await _client.PutAsJsonAsync("/api/vault/profile", profile, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task PutProfile_NoWrappedKek_Returns400()
    {
        var profile = NewValidProfile(bothFactors: true) with
        {
            WrappedKek = new WrappedKek { Passkey = null, RecoveryCode = null }
        };
        var put = await _client.PutAsJsonAsync("/api/vault/profile", profile, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task PutProfile_PasskeyWrappedButNoCredentialIds_Returns400()
    {
        var profile = NewValidProfile(bothFactors: false, includeRecovery: false) with
        {
            PasskeyCredentialIds = []
        };
        var put = await _client.PutAsJsonAsync("/api/vault/profile", profile, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    // --- Helpers ---

    private static VaultProfile NewValidProfile(bool bothFactors, bool includeRecovery = true) => new()
    {
        Salt = RandomBase64(16),
        PasskeyCredentialIds = bothFactors || !includeRecovery ? [RandomBase64(32)] : [],
        WrappedKek = new WrappedKek
        {
            Passkey = bothFactors || !includeRecovery ? RandomBase64(48) : null,
            RecoveryCode = bothFactors || includeRecovery ? RandomBase64(48) : null
        },
        AlgVersion = 1
    };

    private static string RandomBase64(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));
}
