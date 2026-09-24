using GodMode.ClientBase.Services;
using GodMode.ClientBase.Services.Models;
using Microsoft.Extensions.Logging.Abstractions;
using SignalR.Proxy;

namespace GodMode.Relay.Tests;

/// <summary>The directory the relay and the list are built on: one registration can't take the others down.</summary>
public sealed class ServerDirectoryTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"godmode-directory-{Guid.NewGuid():N}");
    private readonly InMemorySecretStore _secrets = new();
    private FakeUpstream _alpha = null!;
    private FakeUpstream _beta = null!;
    private ServerRegistryService _registry = null!;
    private ServerDirectory _directory = null!;

    public async Task InitializeAsync()
    {
        _alpha = await FakeUpstream.StartAsync("alpha");
        _beta = await FakeUpstream.StartAsync("beta");
        _registry = new ServerRegistryService(_dataDir, _secrets);
        _directory = new ServerDirectory(_registry, new ServerUrlSelector(ServerUrlSelector.CreateHttpClient()), NullLoggerFactory.Instance);
    }

    public async Task DisposeAsync()
    {
        await _alpha.DisposeAsync();
        await _beta.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task A_token_that_cannot_be_read_leaves_that_server_out_and_the_others_listed_and_resolvable()
    {
        var alpha = await _registry.AddServerAsync(new ServerRegistration { Urls = [_alpha.Url] }, "key-a");
        var beta = await _registry.AddServerAsync(new ServerRegistration { Urls = [_beta.Url] }, "key-b");
        _secrets.MakeUnreadable(ServerRegistryService.TokenKey(alpha.Id));

        Assert.Equal([beta.Id], (await _directory.ListAllServersAsync()).Select(s => s.Id));
        Assert.Equal([(alpha.Id, false), (beta.Id, true)],
            (await _directory.ListByRegistrationAsync()).Select(l => (l.RegistrationId, Listed: l.Servers is not null)));
        Assert.Null(await _directory.ResolveAsync(alpha.Id));
        Assert.Equal(new RelayTarget($"{_beta.Url}/hubs/projects", "key-b"), await _directory.ResolveAsync(beta.Id));
    }
}
