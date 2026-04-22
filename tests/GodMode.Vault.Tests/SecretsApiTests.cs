using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GodMode.Vault.Models;

namespace GodMode.Vault.Tests;

[Collection("Vault")]
public class SecretsApiTests(VaultFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // --- Store + Get round-trip ---

    [Fact]
    public async Task StoreAndGet_TextSecret_RoundTrips()
    {
        var secret = "sk-ant-super-secret-key"u8.ToArray();
        var b64 = Convert.ToBase64String(secret);

        var storeResponse = await _client.PutAsJsonAsync(
            "/api/secrets/test-profile/API_KEY",
            new StoreSecretRequest { ValueBase64 = b64 });
        Assert.Equal(HttpStatusCode.NoContent, storeResponse.StatusCode);

        var getResponse = await _client.GetAsync("/api/secrets/test-profile/API_KEY");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var returned = await getResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(secret, returned);
    }

    [Fact]
    public async Task StoreAndGet_BinarySecret_RoundTrips()
    {
        var binary = new byte[256];
        for (var i = 0; i < binary.Length; i++) binary[i] = (byte)i;

        var storeResponse = await _client.PutAsJsonAsync(
            "/api/secrets/binary-profile/CERT_FILE",
            new StoreSecretRequest { ValueBase64 = Convert.ToBase64String(binary) });
        Assert.Equal(HttpStatusCode.NoContent, storeResponse.StatusCode);

        var getResponse = await _client.GetAsync("/api/secrets/binary-profile/CERT_FILE");
        var returned = await getResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(binary, returned);
    }

    [Fact]
    public async Task StoreRaw_BinaryBody_RoundTrips()
    {
        var binary = new byte[] { 0x00, 0xFF, 0x42, 0xDE, 0xAD };
        var content = new ByteArrayContent(binary);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        var storeResponse = await _client.PutAsync("/api/secrets/raw-profile/RAW_KEY/raw", content);
        Assert.Equal(HttpStatusCode.NoContent, storeResponse.StatusCode);

        var getResponse = await _client.GetAsync("/api/secrets/raw-profile/RAW_KEY");
        var returned = await getResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(binary, returned);
    }

    // --- Store overwrites existing ---

    [Fact]
    public async Task Store_OverwritesExistingSecret()
    {
        var v1 = Convert.ToBase64String("version1"u8.ToArray());
        var v2 = Convert.ToBase64String("version2"u8.ToArray());

        await _client.PutAsJsonAsync("/api/secrets/overwrite-profile/TOKEN",
            new StoreSecretRequest { ValueBase64 = v1 });

        await _client.PutAsJsonAsync("/api/secrets/overwrite-profile/TOKEN",
            new StoreSecretRequest { ValueBase64 = v2 });

        var getResponse = await _client.GetAsync("/api/secrets/overwrite-profile/TOKEN");
        var returned = await getResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal("version2"u8.ToArray(), returned);
    }

    // --- Get nonexistent ---

    [Fact]
    public async Task Get_NonexistentSecret_Returns404()
    {
        var response = await _client.GetAsync("/api/secrets/nope-profile/DOESNT_EXIST");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- Delete ---

    [Fact]
    public async Task Delete_RemovesSecret()
    {
        await _client.PutAsJsonAsync("/api/secrets/del-profile/TO_DELETE",
            new StoreSecretRequest { ValueBase64 = Convert.ToBase64String("bye"u8.ToArray()) });

        var deleteResponse = await _client.DeleteAsync("/api/secrets/del-profile/TO_DELETE");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getResponse = await _client.GetAsync("/api/secrets/del-profile/TO_DELETE");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task Delete_NonexistentSecret_Returns204()
    {
        var response = await _client.DeleteAsync("/api/secrets/del-profile/NEVER_EXISTED");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // --- Metadata ---

    [Fact]
    public async Task GetMeta_ReturnsCreationTimeAndTtl()
    {
        await _client.PutAsJsonAsync("/api/secrets/meta-profile/TTL_KEY",
            new StoreSecretRequest { ValueBase64 = Convert.ToBase64String("value"u8.ToArray()), Ttl = "90d" });

        var response = await _client.GetAsync("/api/secrets/meta-profile/TTL_KEY/meta");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var meta = await response.Content.ReadFromJsonAsync<SecretMetadata>(JsonOptions);
        Assert.NotNull(meta);
        Assert.Equal("TTL_KEY", meta.Name);
        Assert.NotNull(meta.Ttl);
        Assert.Equal(TimeSpan.FromDays(90), meta.Ttl.Value);
        Assert.False(meta.IsExpired);
    }

    [Fact]
    public async Task GetMeta_NoTtl_ExpiresAtIsNull()
    {
        await _client.PutAsJsonAsync("/api/secrets/meta-profile/NO_TTL",
            new StoreSecretRequest { ValueBase64 = Convert.ToBase64String("value"u8.ToArray()) });

        var response = await _client.GetAsync("/api/secrets/meta-profile/NO_TTL/meta");
        var meta = await response.Content.ReadFromJsonAsync<SecretMetadata>(JsonOptions);
        Assert.NotNull(meta);
        Assert.Null(meta.Ttl);
        Assert.Null(meta.ExpiresAt);
    }

    // --- List ---

    [Fact]
    public async Task ListProfiles_ReturnsStoredProfiles()
    {
        await _client.PutAsJsonAsync("/api/secrets/list-prof-a/KEY1",
            new StoreSecretRequest { ValueBase64 = Convert.ToBase64String("a"u8.ToArray()) });
        await _client.PutAsJsonAsync("/api/secrets/list-prof-b/KEY2",
            new StoreSecretRequest { ValueBase64 = Convert.ToBase64String("b"u8.ToArray()) });

        var response = await _client.GetAsync("/api/secrets/profiles");
        var profiles = await response.Content.ReadFromJsonAsync<List<string>>();
        Assert.NotNull(profiles);
        Assert.Contains("list-prof-a", profiles);
        Assert.Contains("list-prof-b", profiles);
    }

    [Fact]
    public async Task ListSecrets_ReturnsSecretNames()
    {
        await _client.PutAsJsonAsync("/api/secrets/list-sec/ALPHA",
            new StoreSecretRequest { ValueBase64 = Convert.ToBase64String("a"u8.ToArray()) });
        await _client.PutAsJsonAsync("/api/secrets/list-sec/BRAVO",
            new StoreSecretRequest { ValueBase64 = Convert.ToBase64String("b"u8.ToArray()) });

        var response = await _client.GetAsync("/api/secrets/list-sec");
        var secrets = await response.Content.ReadFromJsonAsync<List<string>>();
        Assert.NotNull(secrets);
        Assert.Contains("ALPHA", secrets);
        Assert.Contains("BRAVO", secrets);
    }

    // --- Blank value rejection ---

    [Fact]
    public async Task Store_BlankBase64_Returns400()
    {
        var response = await _client.PutAsJsonAsync("/api/secrets/blank-profile/KEY",
            new StoreSecretRequest { ValueBase64 = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Store_NullBase64_Returns400()
    {
        var response = await _client.PutAsJsonAsync("/api/secrets/blank-profile/KEY",
            new StoreSecretRequest { ValueBase64 = null });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Store_InvalidBase64_Returns400()
    {
        var response = await _client.PutAsJsonAsync("/api/secrets/blank-profile/KEY",
            new StoreSecretRequest { ValueBase64 = "not-valid-base64!!!" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Store_InvalidTtl_Returns400()
    {
        var response = await _client.PutAsJsonAsync("/api/secrets/ttl-profile/KEY",
            new StoreSecretRequest { ValueBase64 = Convert.ToBase64String("v"u8.ToArray()), Ttl = "garbage" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task StoreRaw_EmptyBody_Returns400()
    {
        var content = new ByteArrayContent([]);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        var response = await _client.PutAsync("/api/secrets/raw-profile/EMPTY/raw", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
