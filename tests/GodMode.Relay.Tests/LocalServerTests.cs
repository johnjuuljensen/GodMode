using System.Net;
using System.Net.WebSockets;
using GodMode.ClientBase.Services;
using GodMode.ClientBase.Services.Models;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;
using SignalR.Proxy;

namespace GodMode.Relay.Tests;

/// <summary>
/// The MAUI relay: only the WebView's origin with the per-launch secret gets through,
/// it serves nothing but the WebSocket relay, and it adds each server's own key upstream.
/// </summary>
public sealed class LocalServerTests : IAsyncLifetime
{
    private const string WebViewOrigin = "https://0.0.0.1";

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"godmode-relay-{Guid.NewGuid():N}");
    private readonly InMemorySecretStore _secrets = new();
    private readonly HttpClient _http = new();
    private FakeUpstream _alpha = null!;
    private FakeUpstream _beta = null!;
    private ServerRegistryService _registry = null!;
    private LocalServer _relay = null!;

    public async Task InitializeAsync()
    {
        _alpha = await FakeUpstream.StartAsync("alpha");
        _beta = await FakeUpstream.StartAsync("beta");
        _registry = new ServerRegistryService(_dataDir, _secrets);
        var directory = new ServerDirectory(_registry, new ServerUrlSelector(ServerUrlSelector.CreateHttpClient()), NullLoggerFactory.Instance);
        _relay = new LocalServer(directory.ResolveAsync, [WebViewOrigin], NullLoggerFactory.Instance);
        _relay.Start();
    }

    public async Task DisposeAsync()
    {
        await _relay.DisposeAsync();
        await _alpha.DisposeAsync();
        await _beta.DisposeAsync();
        _http.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    private Task<ServerRegistration> AddServerAsync(string key, params string[] urls) =>
        _registry.AddServerAsync(new ServerRegistration { Type = ServerTypes.Local, Urls = urls }, key);

    private string RelayUrl(string serverId, string? secret) =>
        $"{_relay.BaseUrl}/?serverId={Uri.EscapeDataString(serverId)}" +
        (secret == null ? "" : $"&{LocalServer.SecretQueryKey}={Uri.EscapeDataString(secret)}");

    private async Task<HttpStatusCode> GetAsync(string url, string? origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (origin != null) request.Headers.Add("Origin", origin);
        using var response = await _http.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>A WebSocket upgrade to the relay, as the WebView makes it; returns the HTTP status it was refused with.</summary>
    private async Task<HttpStatusCode> WebSocketStatusAsync(string url, string? origin)
    {
        using var ws = new ClientWebSocket();
        ws.Options.CollectHttpResponseDetails = true;
        if (origin != null) ws.Options.SetRequestHeader("Origin", origin);
        try
        {
            await ws.ConnectAsync(new Uri(url.Replace("http://", "ws://")), CancellationToken.None);
            return ws.HttpStatusCode;
        }
        catch (WebSocketException)
        {
            return ws.HttpStatusCode;
        }
    }

    private async Task<HubConnection> ConnectThroughRelayAsync(string serverId)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(RelayUrl(serverId, _relay.Secret), options =>
            {
                options.SkipNegotiation = true;
                options.Transports = HttpTransportType.WebSockets;
                options.Headers["Origin"] = WebViewOrigin;
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    [Fact]
    public async Task Request_without_the_secret_is_refused_with_401()
    {
        var server = await AddServerAsync("key-a", _alpha.Url);

        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(RelayUrl(server.Id, null), WebViewOrigin));
        Assert.Equal(HttpStatusCode.Unauthorized, await WebSocketStatusAsync(RelayUrl(server.Id, null), WebViewOrigin));
        Assert.Empty(_alpha.HubRequests);
    }

    [Fact]
    public async Task Request_with_a_wrong_secret_is_refused_with_401()
    {
        var server = await AddServerAsync("key-a", _alpha.Url);

        Assert.Equal(HttpStatusCode.Unauthorized, await WebSocketStatusAsync(RelayUrl(server.Id, "not-the-secret"), WebViewOrigin));
        Assert.Empty(_alpha.HubRequests);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost:5173")]
    [InlineData("null")]
    public async Task Request_from_a_foreign_origin_is_refused_with_403_even_with_the_secret(string origin)
    {
        var server = await AddServerAsync("key-a", _alpha.Url);

        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(RelayUrl(server.Id, _relay.Secret), origin));
        Assert.Equal(HttpStatusCode.Forbidden, await WebSocketStatusAsync(RelayUrl(server.Id, _relay.Secret), origin));
        Assert.Empty(_alpha.HubRequests);
    }

    [Fact]
    public async Task Request_without_an_origin_is_refused_with_403()
    {
        var server = await AddServerAsync("key-a", _alpha.Url);

        Assert.Equal(HttpStatusCode.Forbidden, await WebSocketStatusAsync(RelayUrl(server.Id, _relay.Secret), null));
    }

    [Theory]
    [InlineData("/servers")]
    [InlineData("/servers/registrations")]
    [InlineData("/events")]
    [InlineData("/devtools")]
    [InlineData("/")]
    public async Task Only_the_websocket_relay_is_served(string path)
    {
        var url = $"{_relay.BaseUrl}{path}?{LocalServer.SecretQueryKey}={Uri.EscapeDataString(_relay.Secret)}";

        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(url, WebViewOrigin));
    }

    [Fact]
    public async Task Unregistered_server_id_is_refused_with_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, await WebSocketStatusAsync(RelayUrl("not-registered", _relay.Secret), WebViewOrigin));
    }

    [Fact]
    public async Task Relay_adds_the_servers_key_upstream_and_does_not_forward_the_relay_secret()
    {
        var server = await AddServerAsync("key-a", _alpha.Url);

        await using var connection = await ConnectThroughRelayAsync(server.Id);

        Assert.Equal("alpha:hi", await connection.InvokeAsync<string>("Echo", "hi"));
        Assert.NotEmpty(_alpha.HubRequests);
        Assert.All(_alpha.HubRequests, r =>
        {
            Assert.Equal("Bearer key-a", r.Authorization);
            Assert.DoesNotContain(Uri.EscapeDataString(_relay.Secret), r.Query);
        });
    }

    [Fact]
    public async Task Two_servers_each_get_their_own_connection_and_key()
    {
        var a = await AddServerAsync("key-a", _alpha.Url);
        var b = await AddServerAsync("key-b", _beta.Url);

        await using var toA = await ConnectThroughRelayAsync(a.Id);
        await using var toB = await ConnectThroughRelayAsync(b.Id);

        Assert.Equal("alpha:x", await toA.InvokeAsync<string>("Echo", "x"));
        Assert.Equal("beta:x", await toB.InvokeAsync<string>("Echo", "x"));
        Assert.All(_alpha.HubRequests, r => Assert.Equal("Bearer key-a", r.Authorization));
        Assert.All(_beta.HubRequests, r => Assert.Equal("Bearer key-b", r.Authorization));
    }

    [Fact]
    public async Task Entry_with_an_unreachable_first_url_connects_via_the_second()
    {
        var server = await AddServerAsync("key-b", Net.UnreachableUrl(), _beta.Url);

        await using var connection = await ConnectThroughRelayAsync(server.Id);

        Assert.Equal("beta:x", await connection.InvokeAsync<string>("Echo", "x"));
    }

    [Fact]
    public async Task DropAllRelays_closes_active_relays_so_clients_reconnect()
    {
        var server = await AddServerAsync("key-a", _alpha.Url);
        await using var connection = await ConnectThroughRelayAsync(server.Id);
        var closed = new TaskCompletionSource();
        connection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };

        _relay.DropAllRelays();

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
