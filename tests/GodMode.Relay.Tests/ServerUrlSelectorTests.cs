using System.Diagnostics;
using GodMode.ClientBase.Services;

namespace GodMode.Relay.Tests;

public sealed class ServerUrlSelectorTests : IAsyncLifetime
{
    private readonly ServerUrlSelector _selector = new(new HttpClient());
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

    [Fact]
    public async Task Trailing_slash_is_dropped()
    {
        Assert.Equal(_first.Url, await _selector.SelectAsync([_first.Url + "/"]));
    }
}
