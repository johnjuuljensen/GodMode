using GodMode.ClientBase.Services;
using GodMode.ClientBase.Services.Models;

namespace GodMode.Relay.Tests;

public sealed class ServerRegistryTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"godmode-registry-{Guid.NewGuid():N}");
    private readonly InMemorySecretStore _secrets = new();

    private string ServersFile => Path.Combine(_dataDir, "servers.json");

    public ServerRegistryTests() => Directory.CreateDirectory(_dataDir);

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Each_server_gets_its_own_id_and_its_token_stays_out_of_the_file()
    {
        var registry = new ServerRegistryService(_dataDir, _secrets);

        var a = await registry.AddServerAsync(new ServerRegistration { Urls = ["http://10.0.2.2:31337"] }, "secret-key-a");
        var b = await registry.AddServerAsync(new ServerRegistration { Urls = ["http://10.0.2.2:31338/"] }, "secret-key-b");

        Assert.NotEqual(a.Id, b.Id);
        Assert.True(Guid.TryParse(a.Id, out _));
        Assert.Equal(["http://10.0.2.2:31338"], b.Urls);
        Assert.Equal("secret-key-a", await registry.GetAccessTokenAsync(a.Id));
        Assert.Equal("secret-key-b", await registry.GetAccessTokenAsync(b.Id));

        var file = await File.ReadAllTextAsync(ServersFile);
        Assert.DoesNotContain("secret-key", file);
        Assert.DoesNotContain("Token", file);

        var reloaded = await new ServerRegistryService(_dataDir, _secrets).GetServersAsync();
        Assert.Equal([a.Id, b.Id], reloaded.Select(s => s.Id));
    }

    [Fact]
    public async Task Removing_a_server_removes_its_token()
    {
        var registry = new ServerRegistryService(_dataDir, _secrets);
        var a = await registry.AddServerAsync(new ServerRegistration { Urls = ["http://a"] }, "key");

        Assert.True(await registry.RemoveServerAsync(a.Id));

        Assert.Empty(await registry.GetServersAsync());
        Assert.Empty(_secrets.Values);
        Assert.False(await registry.RemoveServerAsync(a.Id));
    }

    [Fact]
    public async Task Github_token_is_stored_once_as_given()
    {
        var registry = new ServerRegistryService(_dataDir, _secrets);

        var gh = await registry.AddServerAsync(new ServerRegistration { Type = ServerTypes.GitHub, Username = "octo" }, "ghp_abc");

        Assert.Equal("ghp_abc", await registry.GetAccessTokenAsync(gh.Id));
    }

    [Fact]
    public async Task Older_file_is_upgraded_ids_urls_and_tokens_moved_to_secure_storage()
    {
        await File.WriteAllTextAsync(ServersFile, """
            {
              "Servers": [
                { "Type": "local", "Url": "http://localhost:31337/", "Token": "plain:local-key", "DisplayName": "One" },
                { "Type": "local", "Url": "http://localhost:31338", "DisplayName": "Two" },
                { "Type": "github", "Username": "octo", "Token": "plain:plain:ghp_double" }
              ]
            }
            """);

        var servers = await new ServerRegistryService(_dataDir, _secrets).GetServersAsync();

        Assert.Equal(3, servers.Select(s => s.Id).Distinct().Count());
        Assert.Equal(["http://localhost:31337"], servers[0].Urls);
        Assert.Equal(["http://localhost:31338"], servers[1].Urls);
        Assert.All(servers, s => Assert.Null(s.Token));
        var registry = new ServerRegistryService(_dataDir, _secrets);
        Assert.Equal("local-key", await registry.GetAccessTokenAsync(servers[0].Id));
        Assert.Null(await registry.GetAccessTokenAsync(servers[1].Id));
        Assert.Equal("ghp_double", await registry.GetAccessTokenAsync(servers[2].Id));

        var file = await File.ReadAllTextAsync(ServersFile);
        Assert.DoesNotContain("local-key", file);
        Assert.DoesNotContain("ghp_double", file);
        Assert.DoesNotContain("\"Url\"", file);

        var reloaded = await new ServerRegistryService(_dataDir, _secrets).GetServersAsync();
        Assert.Equal(servers.Select(s => s.Id), reloaded.Select(s => s.Id));
    }
}
