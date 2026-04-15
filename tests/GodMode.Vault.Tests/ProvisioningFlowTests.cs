using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GodMode.Vault.Models;

namespace GodMode.Vault.Tests;

/// <summary>
/// Tests the provisioning flow: check → (store missing) → fetch.
/// </summary>
[Collection("Vault")]
public class ProvisioningFlowTests(VaultFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public async Task Check_NoSecretsStored_ReportsAllMissing()
    {
        var request = new ProfileCheckRequest("prov-empty", ["KEY_A", "KEY_B", "KEY_C"]);
        var response = await _client.PostAsJsonAsync("/api/secrets/check", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ProfileCheckResult>(JsonOptions);
        Assert.NotNull(result);
        Assert.False(result.Ready);
        Assert.Equal(3, result.Secrets.Count);
        Assert.All(result.Secrets, s => Assert.False(s.Exists));
    }

    [Fact]
    public async Task Check_PartialSecrets_ReportsNotReady()
    {
        await _client.PutAsJsonAsync("/api/secrets/prov-partial/KEY_A",
            new StoreSecretRequest { ValueBase64 = Convert.ToBase64String("a"u8.ToArray()) });

        var request = new ProfileCheckRequest("prov-partial", ["KEY_A", "KEY_B"]);
        var response = await _client.PostAsJsonAsync("/api/secrets/check", request);

        var result = await response.Content.ReadFromJsonAsync<ProfileCheckResult>(JsonOptions);
        Assert.NotNull(result);
        Assert.False(result.Ready);

        var keyA = result.Secrets.Single(s => s.Name == "KEY_A");
        var keyB = result.Secrets.Single(s => s.Name == "KEY_B");
        Assert.True(keyA.Exists);
        Assert.False(keyB.Exists);
    }

    [Fact]
    public async Task Check_AllSecretsStored_ReportsReady()
    {
        await _client.PutAsJsonAsync("/api/secrets/prov-ready/KEY_A",
            new StoreSecretRequest { ValueBase64 = Convert.ToBase64String("a"u8.ToArray()) });
        await _client.PutAsJsonAsync("/api/secrets/prov-ready/KEY_B",
            new StoreSecretRequest { ValueBase64 = Convert.ToBase64String("b"u8.ToArray()) });

        var request = new ProfileCheckRequest("prov-ready", ["KEY_A", "KEY_B"]);
        var response = await _client.PostAsJsonAsync("/api/secrets/check", request);

        var result = await response.Content.ReadFromJsonAsync<ProfileCheckResult>(JsonOptions);
        Assert.NotNull(result);
        Assert.True(result.Ready);
        Assert.All(result.Secrets, s => Assert.True(s.Exists));
    }

    [Fact]
    public async Task Fetch_AllPresent_ReturnsBase64Values()
    {
        var valueA = "secret-a-value"u8.ToArray();
        var valueB = "secret-b-value"u8.ToArray();

        await _client.PutAsJsonAsync("/api/secrets/prov-fetch/KEY_A",
            new StoreSecretRequest { ValueBase64 = Convert.ToBase64String(valueA) });
        await _client.PutAsJsonAsync("/api/secrets/prov-fetch/KEY_B",
            new StoreSecretRequest { ValueBase64 = Convert.ToBase64String(valueB) });

        var request = new ProfileCheckRequest("prov-fetch", ["KEY_A", "KEY_B"]);
        var response = await _client.PostAsJsonAsync("/api/secrets/fetch", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var values = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(JsonOptions);
        Assert.NotNull(values);
        Assert.Equal(valueA, Convert.FromBase64String(values["KEY_A"]));
        Assert.Equal(valueB, Convert.FromBase64String(values["KEY_B"]));
    }

    [Fact]
    public async Task Fetch_MissingSecrets_Returns409WithCheckResult()
    {
        await _client.PutAsJsonAsync("/api/secrets/prov-incomplete/KEY_A",
            new StoreSecretRequest { ValueBase64 = Convert.ToBase64String("a"u8.ToArray()) });

        var request = new ProfileCheckRequest("prov-incomplete", ["KEY_A", "KEY_B"]);
        var response = await _client.PostAsJsonAsync("/api/secrets/fetch", request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ProfileCheckResult>(JsonOptions);
        Assert.NotNull(result);
        Assert.False(result.Ready);
    }

    // --- Full provisioning cycle: check → store missing → fetch ---

    [Fact]
    public async Task FullProvisioningCycle_CheckStoreFetch()
    {
        var schema = new[] { "ANTHROPIC_API_KEY", "JIRA_TOKEN", "GITHUB_PAT" };
        var profile = "full-cycle";

        // Step 1: Check — nothing stored yet
        var checkReq = new ProfileCheckRequest(profile, schema);
        var checkResp = await _client.PostAsJsonAsync("/api/secrets/check", checkReq);
        var check = await checkResp.Content.ReadFromJsonAsync<ProfileCheckResult>(JsonOptions);
        Assert.NotNull(check);
        Assert.False(check.Ready);

        // Step 2: Store the missing secrets
        var secrets = new Dictionary<string, string>
        {
            ["ANTHROPIC_API_KEY"] = "sk-ant-key-123",
            ["JIRA_TOKEN"] = "jira-pat-456",
            ["GITHUB_PAT"] = "ghp_789"
        };

        foreach (var (name, value) in secrets)
        {
            var resp = await _client.PutAsJsonAsync($"/api/secrets/{profile}/{name}",
                new StoreSecretRequest { ValueBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)) });
            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        }

        // Step 3: Check again — should be ready
        checkResp = await _client.PostAsJsonAsync("/api/secrets/check", checkReq);
        check = await checkResp.Content.ReadFromJsonAsync<ProfileCheckResult>(JsonOptions);
        Assert.NotNull(check);
        Assert.True(check.Ready);

        // Step 4: Fetch — should return all values
        var fetchResp = await _client.PostAsJsonAsync("/api/secrets/fetch", checkReq);
        Assert.Equal(HttpStatusCode.OK, fetchResp.StatusCode);
        var fetched = await fetchResp.Content.ReadFromJsonAsync<Dictionary<string, string>>(JsonOptions);
        Assert.NotNull(fetched);

        foreach (var (name, expected) in secrets)
        {
            var actual = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(fetched[name]));
            Assert.Equal(expected, actual);
        }
    }
}
