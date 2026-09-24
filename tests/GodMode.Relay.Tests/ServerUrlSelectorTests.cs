using System.Diagnostics;
using GodMode.ClientBase.Services;

namespace GodMode.Relay.Tests;

public sealed class ServerUrlSelectorTests : IAsyncLifetime
{
    private readonly ServerUrlSelector _selector = new(ServerUrlSelector.CreateHttpClient());
    private FakeUpstream _first = null!;
    private FakeUpstream _second = null!;

    public async Task InitializeAsync()
    {
        _first = await FakeUpstream.StartAsync("first");
        _second = await FakeUpstream.StartAsync("second");
    }

    public async Task DisposeAsync()
    {
        await _first.DisposeAsync();
        await _second.DisposeAsync();
    }

    [Fact]
    public async Task First_unreachable_second_reachable_picks_the_second()
    {
        Assert.Equal(_second.Url, await _selector.SelectAsync([Net.UnreachableUrl(), _second.Url]));
    }

    [Fact]
    public async Task First_that_never_answers_is_given_up_within_the_timeout()
    {
        var (blackHole, listener) = Net.BlackHole();
        using var _ = listener;
        var watch = Stopwatch.StartNew();

        Assert.Equal(_second.Url, await _selector.SelectAsync([blackHole, _second.Url]));
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, ServerUrlSelector.DefaultTimeout + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Both_reachable_picks_the_first_in_the_entrys_order()
    {
        Assert.Equal(_first.Url, await _selector.SelectAsync([_first.Url, _second.Url]));
        Assert.Equal(_second.Url, await _selector.SelectAsync([_second.Url, _first.Url]));
    }

    [Fact]
    public async Task None_reachable_gives_null()
    {
        Assert.Null(await _selector.SelectAsync([Net.UnreachableUrl(), Net.UnreachableUrl()]));
    }

    /// <summary>
    /// "localhost" resolves to ::1 before 127.0.0.1; on Windows a refused connect to ::1 takes about 2 s,
    /// longer than the timeout, so trying the addresses in turn would call a running server unreachable.
    /// </summary>
    [Fact]
    public async Task Localhost_url_of_a_server_on_127_0_0_1_answers_within_the_timeout()
    {
        var localhostUrl = _first.Url.Replace("127.0.0.1", "localhost");

        Assert.Equal(localhostUrl, await _selector.SelectAsync([localhostUrl]));
    }

    [Fact]
    public async Task Trailing_slash_is_dropped()
    {
        Assert.Equal(_first.Url, await _selector.SelectAsync([_first.Url + "/"]));
    }
}
