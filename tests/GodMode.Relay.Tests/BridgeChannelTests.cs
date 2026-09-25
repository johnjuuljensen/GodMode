using System.Text.Json;
using GodMode.ClientBase.Bridge;
using GodMode.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodMode.Relay.Tests;

/// <summary>
/// The bridge answers only the app's own page. A page the WebView was sent to instead gets no reply and changes
/// nothing: it cannot read the relay's secret or remove a server.
/// </summary>
public sealed class BridgeChannelTests
{
    private const string AppPage = "https://0.0.0.1/";

    private sealed record Secret(string Value);

    /// <summary>The bridge over a page whose address a test sets; the UI thread is the test's.</summary>
    private sealed class Page(string? address) : BridgeChannel(NullLogger.Instance)
    {
        public string? Address { get; set; } = address;
        public List<BridgeMessage> Posted { get; } = [];
        public int UiCalls { get; private set; }

        public void Sends(BridgeMessage message) => Receive(JsonSerializer.Serialize(message, JsonDefaults.Compact));

        protected override string? PageAddress => Address;
        protected override void PostToPage(string message) => Posted.Add(JsonSerializer.Deserialize<BridgeMessage>(message, JsonDefaults.Options)!);

        protected override void OnUiThread(Action action)
        {
            UiCalls++;
            action();
        }
    }

    private static Page WithSecret(string? address)
    {
        var page = new Page(address);
        page.Handle("relay.info", () => Task.FromResult(new Secret("s3cret")));
        return page;
    }

    [Fact]
    public void The_apps_page_gets_its_answer()
    {
        var page = WithSecret(AppPage);

        page.Sends(new BridgeMessage("relay.info", "1"));

        var reply = Assert.Single(page.Posted);
        Assert.Equal(("relay.info", "1"), (reply.Type, reply.Id));
        Assert.Equal("s3cret", reply.Payload?.Deserialize<Secret>(JsonDefaults.Options)?.Value);
    }

    [Theory]
    [InlineData("https://example.invalid/")]
    [InlineData("https://0.0.0.1.example.invalid/")]
    [InlineData("http://0.0.0.1/")]
    [InlineData("about:blank")]
    [InlineData(null)]
    public void Another_page_gets_no_reply_and_changes_nothing(string? address)
    {
        var page = WithSecret(address);
        var removed = false;
        page.Handle<string, bool>("servers.remove", _ => Task.FromResult(removed = true));
        var events = 0;
        page.MessageReceived += _ => events++;

        page.Sends(new BridgeMessage("relay.info", "1"));
        page.Sends(new BridgeMessage("servers.remove", "2", JsonSerializer.SerializeToElement("server", JsonDefaults.Options)));
        page.Sends(new BridgeMessage("some.event"));

        Assert.Empty(page.Posted);
        Assert.False(removed);
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task Another_page_cannot_answer_the_hosts_request()
    {
        var page = new Page(AppPage);
        var request = page.RequestAsync<string>("host.ask");
        var id = Assert.Single(page.Posted).Id;

        page.Address = "https://example.invalid/";
        page.Sends(new BridgeMessage("host.ask", id, JsonSerializer.SerializeToElement("forged", JsonDefaults.Options)));

        page.Address = AppPage;
        page.Sends(new BridgeMessage("host.ask", id, JsonSerializer.SerializeToElement("real", JsonDefaults.Options)));
        Assert.Equal("real", await request);
    }

    [Fact]
    public void Nothing_is_sent_to_another_page()
    {
        var page = new Page("https://example.invalid/");

        page.Send("servers.changed");
        Assert.Empty(page.Posted);

        page.Address = AppPage;
        page.Send("servers.changed");
        Assert.Equal("servers.changed", Assert.Single(page.Posted).Type);
    }

    [Theory]
    [InlineData(AppPage, 1)]
    [InlineData("https://example.invalid/", 0)]
    public void A_reply_goes_only_to_the_page_shown_when_it_is_ready(string shownThen, int replies)
    {
        var page = new Page(AppPage);
        var answer = new TaskCompletionSource<Secret>();
        page.Handle("relay.info", () => answer.Task);

        page.Sends(new BridgeMessage("relay.info", "1"));
        page.Address = shownThen;
        answer.SetResult(new Secret("s3cret"));

        // The request was one call on the UI thread, and the reply is another, whether or not it is posted
        Assert.True(SpinWait.SpinUntil(() => page.UiCalls == 2, TimeSpan.FromSeconds(5)));
        Assert.Equal(replies, page.Posted.Count);
    }
}
