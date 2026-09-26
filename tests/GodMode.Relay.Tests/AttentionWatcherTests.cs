using GodMode.ClientBase.Attention;
using GodMode.ClientBase.Services;
using GodMode.ClientBase.Services.Models;
using Microsoft.Extensions.Logging.Abstractions;
using static GodMode.Relay.Tests.Attention;

namespace GodMode.Relay.Tests;

/// <summary>
/// The watcher behind the phone's notifications: one direct connection per registered server, with that
/// server's key from the registry, following each server's attention list.
/// </summary>
public sealed class AttentionWatcherTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"godmode-attention-{Guid.NewGuid():N}");
    private readonly InMemorySecretStore _secrets = new();
    private readonly RecordingNotifier _notifier = new();
    private ServerRegistryService _registry = null!;
    private FlakyDirectory _directory = null!;
    private AttentionWatcher _watcher = null!;
    private FakeAttentionServer _alpha = null!;
    private FakeAttentionServer _beta = null!;

    public async Task InitializeAsync()
    {
        _alpha = await FakeAttentionServer.StartAsync();
        _beta = await FakeAttentionServer.StartAsync();
        _registry = new ServerRegistryService(_dataDir, _secrets);
        _directory = new FlakyDirectory(new ServerDirectory(_registry, new ServerUrlSelector(ServerUrlSelector.CreateHttpClient()), NullLoggerFactory.Instance));
        _watcher = new AttentionWatcher(_directory, _notifier, NullLoggerFactory.Instance,
            retryDelay: TimeSpan.FromMilliseconds(50), maxRetryDelay: TimeSpan.FromMilliseconds(200));
    }

    public async Task DisposeAsync()
    {
        await _watcher.DisposeAsync();
        await _alpha.DisposeAsync();
        await _beta.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<string> AddAsync(FakeAttentionServer server, string key) =>
        (await _registry.AddServerAsync(new ServerRegistration { Type = ServerTypes.Local, Urls = [server.Url] }, key)).Id;

    [Fact]
    public async Task What_a_server_lists_when_the_watcher_connects_is_shown_with_its_key_presented()
    {
        _alpha.SetQuietly(Item("Default/root/a"));
        var alpha = await AddAsync(_alpha, "key-alpha");

        await _watcher.RefreshAsync();

        await UntilAsync(() => _notifier.Items.Count == 1, "the listed item");
        Assert.Equal([(alpha, "Default/root/a")], _notifier.Items);
        Assert.All(_alpha.Tokens, t => Assert.Equal("key-alpha", t));
    }

    [Fact]
    public async Task An_item_answered_on_another_device_clears_its_notification()
    {
        var alpha = await AddAsync(_alpha, "key-alpha");
        await _watcher.RefreshAsync();
        await UntilAsync(() => _alpha.Connections == 1, "the connection");

        await _alpha.SetAsync(Item("Default/root/a"), Item("Default/root/b"));
        await UntilAsync(() => _notifier.Items.Count == 2, "both items");

        // Answered in the browser: the server pushes the list without it
        await _alpha.SetAsync(Item("Default/root/b"));
        await UntilAsync(() => _notifier.Items.Count == 1, "the answered item to clear");
        Assert.Equal([(alpha, "Default/root/b")], _notifier.Items);
    }

    [Fact]
    public async Task Two_servers_are_watched_at_once_and_the_same_project_id_on_each_is_its_own_notification()
    {
        var alpha = await AddAsync(_alpha, "key-alpha");
        var beta = await AddAsync(_beta, "key-beta");
        await _watcher.RefreshAsync();
        await UntilAsync(() => _alpha.Connections == 1 && _beta.Connections == 1, "both connections");

        await _alpha.SetAsync(Item("Default/root/p"));
        await _beta.SetAsync(Item("Default/root/p"));
        await UntilAsync(() => _notifier.Items.Count == 2, "an item from each server");
        Assert.Equal(new[] { (alpha, "Default/root/p"), (beta, "Default/root/p") }.Order(), _notifier.Items);

        await _alpha.SetAsync();
        await UntilAsync(() => _notifier.Items.Count == 1, "alpha's item to clear");
        Assert.Equal([(beta, "Default/root/p")], _notifier.Items);
        Assert.All(_beta.Tokens, t => Assert.Equal("key-beta", t));
    }

    [Fact]
    public async Task A_dropped_connection_is_made_again_and_catches_up_on_what_changed_meanwhile()
    {
        var alpha = await AddAsync(_alpha, "key-alpha");
        await _watcher.RefreshAsync();
        await _alpha.SetAsync(Item("Default/root/a"));
        await UntilAsync(() => _notifier.Items.Count == 1, "the item");

        _alpha.SetQuietly(Item("Default/root/b"));
        _alpha.DropConnections();

        await UntilAsync(() => _notifier.Items is [(_, "Default/root/b")], "the list as it is after the reconnect");
        Assert.Equal([(alpha, "Default/root/b")], _notifier.Items);
    }

    [Fact]
    public async Task Reconnect_makes_every_connection_again()
    {
        await AddAsync(_alpha, "key-alpha");
        await _watcher.RefreshAsync();
        await UntilAsync(() => _alpha.Connections == 1, "the connection");

        _watcher.Reconnect();

        await UntilAsync(() => _alpha.Connections == 2, "a second connection");
    }

    [Fact]
    public async Task A_server_that_is_removed_takes_its_notifications_with_it()
    {
        var alpha = await AddAsync(_alpha, "key-alpha");
        var beta = await AddAsync(_beta, "key-beta");
        _alpha.SetQuietly(Item("Default/root/a"));
        _beta.SetQuietly(Item("Default/root/b"));
        await _watcher.RefreshAsync();
        await UntilAsync(() => _notifier.Items.Count == 2, "an item from each server");

        await _registry.RemoveServerAsync(alpha);
        await _watcher.RefreshAsync();

        Assert.Equal([(beta, "Default/root/b")], _notifier.Items);
    }

    [Fact]
    public async Task What_an_earlier_run_left_showing_is_put_right_once_the_servers_are_listed()
    {
        var alpha = await AddAsync(_alpha, "key-alpha");
        _alpha.SetQuietly(Item("Default/root/kept"));
        var notifier = new RecordingNotifier();
        notifier.LeftShowing.AddRange([new(alpha, "Default/root/answered"), new(alpha, "Default/root/kept"), new("unregistered", "Default/root/x")]);
        var directory = new ServerDirectory(_registry, new ServerUrlSelector(ServerUrlSelector.CreateHttpClient()), NullLoggerFactory.Instance);
        await using var watcher = new AttentionWatcher(directory, notifier, NullLoggerFactory.Instance);

        await watcher.RefreshAsync();

        await UntilAsync(() => notifier.Items.Count == 1, "the listed item");
        Assert.Equal([(alpha, "Default/root/kept")], notifier.Items);
        Assert.Contains($"cancel {new AttentionLink(alpha, "Default/root/answered").Key}", notifier.Log);
        Assert.Contains($"cancel {new AttentionLink("unregistered", "Default/root/x").Key}", notifier.Log);
    }

    [Fact]
    public async Task A_listing_that_fails_once_keeps_its_servers_watched_and_their_notifications_shown()
    {
        var alpha = await AddAsync(_alpha, "key-alpha");
        var beta = await AddAsync(_beta, "key-beta");
        _alpha.SetQuietly(Item("Default/root/a"));
        _beta.SetQuietly(Item("Default/root/b"));
        await _watcher.RefreshAsync();
        await UntilAsync(() => _notifier.Items.Count == 2, "an item from each server");

        // A GitHub API error, say: alpha's registration cannot be listed this time
        _directory.FailOnce(alpha);
        await _watcher.RefreshAsync();

        Assert.Equal(2, _notifier.Items.Count);
        Assert.DoesNotContain(_notifier.Log, l => l.StartsWith("cancel"));
        await _alpha.SetAsync(Item("Default/root/a"), Item("Default/root/a2"));
        await UntilAsync(() => _notifier.Items.Count == 3, "alpha's new item, so alpha is still watched");

        await _watcher.RefreshAsync();
        Assert.Equal(3, _notifier.Items.Count);
        Assert.Contains(_notifier.Items, i => i.ServerId == beta);
    }

    [Fact]
    public async Task A_server_that_is_down_does_not_hold_up_the_others()
    {
        var url = Net.UnreachableUrl();
        var down = (await _registry.AddServerAsync(new ServerRegistration { Type = ServerTypes.Local, Urls = [url] }, "key")).Id;
        await AddAsync(_alpha, "key-alpha");
        _alpha.SetQuietly(Item("Default/root/a"));

        await _watcher.RefreshAsync();

        await UntilAsync(() => _notifier.Items.Count == 1, "the reachable server's item");
        Assert.DoesNotContain(_notifier.Items, i => i.ServerId == down);
    }

    [Fact]
    public async Task RefreshAsync_tells_how_many_servers_it_watches()
    {
        await AddAsync(_alpha, "key-alpha");
        var beta = await AddAsync(_beta, "key-beta");

        Assert.Equal(2, await _watcher.RefreshAsync());

        await _registry.RemoveServerAsync(beta);
        Assert.Equal(1, await _watcher.RefreshAsync());
    }

    [Fact]
    public async Task A_registration_whose_token_cannot_be_read_leaves_nothing_to_watch()
    {
        var alpha = await AddAsync(_alpha, "key-alpha");
        _secrets.MakeUnreadable(ServerRegistryService.TokenKey(alpha));

        Assert.Equal(0, await _watcher.RefreshAsync());
        Assert.Equal(0, _alpha.Connections);
    }
}
