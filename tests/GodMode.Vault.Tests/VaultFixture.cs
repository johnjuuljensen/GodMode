using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GodMode.Vault.Services;

namespace GodMode.Vault.Tests;

/// <summary>
/// Shared test fixture that boots the Vault with fake auth, temp storage, and a generated master secret.
/// </summary>
public class VaultFixture : IAsyncLifetime
{
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public string StoragePath { get; private set; } = null!;
    public string MasterSecret { get; } = EncryptionService.GenerateMasterSecret();

    public const string TestUserSub = "google:test-user-12345";
    public const string TestUserName = "testuser@example.com";
    public const string TestProvider = "google";

    public Task InitializeAsync()
    {
        StoragePath = Path.Combine(Path.GetTempPath(), "godmode-vault-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(StoragePath);

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Vault:MasterSecret", MasterSecret);
                builder.UseSetting("Vault:StoragePath", StoragePath);

                builder.ConfigureServices(services =>
                {
                    // Replace the default auth with a test scheme that auto-authenticates
                    services.AddAuthentication("Test")
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });

                    services.PostConfigure<AuthenticationOptions>(options =>
                    {
                        options.DefaultAuthenticateScheme = "Test";
                        options.DefaultChallengeScheme = "Test";
                        options.DefaultScheme = "Test";
                    });

                    // Satisfy Google/GitHub OAuth option validators so they don't throw
                    services.PostConfigure<GoogleOptions>(GoogleDefaults.AuthenticationScheme, o =>
                    {
                        o.ClientId = "fake-test-client-id";
                        o.ClientSecret = "fake-test-client-secret";
                    });
                    services.PostConfigure<OAuthOptions>("GitHub", o =>
                    {
                        o.ClientId = "fake-test-client-id";
                        o.ClientSecret = "fake-test-client-secret";
                    });
                });
            });

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Factory.Dispose();
        if (Directory.Exists(StoragePath))
        {
            try { Directory.Delete(StoragePath, recursive: true); }
            catch { /* best effort cleanup */ }
        }
        return Task.CompletedTask;
    }

    public HttpClient CreateClient() => Factory.CreateClient();
}

/// <summary>
/// Auth handler that auto-authenticates as the test user with Google provider claims.
/// </summary>
public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "test-user-12345"),
            new Claim(ClaimTypes.Name, VaultFixture.TestUserName),
            new Claim(ClaimTypes.Email, VaultFixture.TestUserName),
            new Claim("provider", VaultFixture.TestProvider)
        };

        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, "Test");

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

[CollectionDefinition("Vault")]
public class VaultCollection : ICollectionFixture<VaultFixture>;
