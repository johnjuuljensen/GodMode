using System.Net;
using System.Net.Http.Json;
using GodMode.Vault.Models;
using GodMode.Vault.Services;

namespace GodMode.Vault.Tests;

/// <summary>
/// Tests name validation and path traversal rejection.
/// </summary>
[Collection("Vault")]
public class ValidationTests(VaultFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();

    // --- ValidateName unit tests ---

    [Theory]
    [InlineData("API_KEY")]
    [InlineData("my-secret")]
    [InlineData("a")]
    [InlineData("A123_b-456")]
    [InlineData("ANTHROPIC_API_KEY")]
    public void ValidateName_ValidNames_Accepted(string name)
    {
        Assert.Equal(name, FileSecretStore.ValidateName(name));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../etc/passwd")]
    [InlineData("..\\windows\\system32")]
    [InlineData("secret.name")]
    [InlineData("secret/name")]
    [InlineData("secret\\name")]
    [InlineData("name with spaces")]
    [InlineData("name\ttab")]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateName_InvalidNames_Rejected(string name)
    {
        Assert.Throws<ArgumentException>(() => FileSecretStore.ValidateName(name));
    }

    // --- Path traversal via route parameters ---

    [Theory]
    [InlineData("/api/secrets/good-profile/..%2F..%2Fetc%2Fpasswd")]
    [InlineData("/api/secrets/..%2F..%2Fetc/secretname")]
    [InlineData("/api/secrets/good-profile/..%5C..%5Cwindows")]
    [InlineData("/api/secrets/good-profile/foo.bar")]
    [InlineData("/api/secrets/good-profile/foo%2Fbar")]
    public async Task Get_PathTraversal_Returns400OrNotFound(string path)
    {
        var response = await _client.GetAsync(path);
        // Either 400 (invalid name caught) or 404 (route didn't match)
        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
            $"Expected 400 or 404 but got {(int)response.StatusCode} for {path}");
    }

    [Theory]
    [InlineData("..%2F..%2Fetc%2Fpasswd")]
    [InlineData("..")]
    [InlineData("foo.bar")]
    public async Task Store_PathTraversalSecretName_Returns400(string secretName)
    {
        var response = await _client.PutAsJsonAsync($"/api/secrets/good-profile/{secretName}",
            new StoreSecretRequest { ValueBase64 = Convert.ToBase64String("x"u8.ToArray()) });
        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
            $"Expected 400 or 404 but got {(int)response.StatusCode}");
    }

    [Theory]
    [InlineData("..")]
    [InlineData("foo.bar")]
    public async Task Store_PathTraversalProfileName_Returns400(string profile)
    {
        var response = await _client.PutAsJsonAsync($"/api/secrets/{profile}/GOOD_NAME",
            new StoreSecretRequest { ValueBase64 = Convert.ToBase64String("x"u8.ToArray()) });
        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
            $"Expected 400 or 404 but got {(int)response.StatusCode}");
    }

    [Theory]
    [InlineData("..%2F..%2Fetc%2Fpasswd")]
    [InlineData("..")]
    [InlineData("a.b")]
    public async Task Delete_PathTraversal_Returns400(string secretName)
    {
        var response = await _client.DeleteAsync($"/api/secrets/good-profile/{secretName}");
        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
            $"Expected 400 or 404 but got {(int)response.StatusCode}");
    }

    // --- Path traversal via JSON body ---

    [Fact]
    public async Task Check_PathTraversalInSecretNames_Returns400()
    {
        var request = new ProfileCheckRequest("good-profile", ["VALID_KEY", "../../../etc/passwd"]);
        var response = await _client.PostAsJsonAsync("/api/secrets/check", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Check_PathTraversalInProfile_Returns400()
    {
        var request = new ProfileCheckRequest("../../evil", ["VALID_KEY"]);
        var response = await _client.PostAsJsonAsync("/api/secrets/check", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Fetch_PathTraversalInSecretNames_Returns400()
    {
        var request = new ProfileCheckRequest("good-profile", ["../../../secret"]);
        var response = await _client.PostAsJsonAsync("/api/secrets/fetch", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
