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
    public async Task Server_whose_token_secure_storage_refuses_is_not_added_and_its_token_never_reaches_the_file()
    {
        var kept = await new ServerRegistryService(_dataDir, _secrets).AddServerAsync(new ServerRegistration { Urls = ["http://kept"] }, "key");
        var registry = new ServerRegistryService(_dataDir, new FailingSecretStore());

        var error = await Record.ExceptionAsync(() =>
            registry.AddServerAsync(new ServerRegistration { Type = ServerTypes.GitHub, Username = "octo" }, "ghp_refused"));

        Assert.DoesNotContain("ghp_refused", await File.ReadAllTextAsync(ServersFile));
        Assert.Contains("secure storage", Assert.IsType<InvalidOperationException>(error).Message);
        Assert.Equal([kept.Id], (await new ServerRegistryService(_dataDir, _secrets).GetServersAsync()).Select(s => s.Id));
    }

    /// <summary>Every GodMode server requires its API key, a local one included: a server without one is not added.</summary>
    [Theory]
    [InlineData(ServerTypes.Local, null)]
    [InlineData(ServerTypes.Local, "")]
    [InlineData(ServerTypes.Local, "  ")]
    [InlineData(ServerTypes.GitHub, null)]
    public async Task Server_without_a_key_is_not_added(string type, string? accessToken)
    {
        var registry = new ServerRegistryService(_dataDir, _secrets);

        var error = await Record.ExceptionAsync(() => registry.AddServerAsync(
            new ServerRegistration { Type = type, Urls = ["http://127.0.0.1:31337"], Username = "octo" }, accessToken));

        Assert.IsType<ArgumentException>(error);
        Assert.Empty(await registry.GetServersAsync());
        Assert.False(File.Exists(ServersFile));
        Assert.Empty(_secrets.Values);
    }

    private sealed class FailingSecretStore : ISecretStore
    {
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string value) => throw new InvalidOperationException("secure storage unavailable");
        public bool Remove(string key) => false;
    }
}
