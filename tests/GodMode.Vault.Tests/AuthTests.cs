using System.Net;

namespace GodMode.Vault.Tests;

/// <summary>
/// Tests health and auth endpoints.
/// </summary>
[Collection("Vault")]
public class AuthTests(VaultFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("healthy", body);
    }

    [Fact]
    public async Task AuthMe_Authenticated_ReturnsUserInfo()
    {
        var response = await _client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("true", body);       // authenticated: true
        Assert.Contains("google", body);     // provider
        Assert.Contains("test-user", body);  // sub contains test-user
    }
}
