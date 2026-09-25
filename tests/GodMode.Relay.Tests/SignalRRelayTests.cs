using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using SignalR.Proxy;

namespace GodMode.Relay.Tests;

/// <summary>
/// One relayed connection, against an upstream the test plays byte by byte: what reaches the client, how its socket
/// ends, and that the relay lets go of the services and the connection it built.
/// </summary>
public sealed class SignalRRelayTests : IAsyncLifetime
{
    private const string HandshakeResponse = "{}\x1e";
    private const string CloseMessage = "{\"type\":7,\"allowReconnect\":true}\x1e";
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(5);

    private readonly FakeUpstreamConnection _upstream = new();
    private WebSocket _relaySide = null!;
    private WebSocket _client = null!;

    public async Task InitializeAsync() => (_relaySide, _client) = await WebSocketPair.CreateAsync();

    public Task DisposeAsync()
    {
        _relaySide.Dispose();
        _client.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>A builder whose connection factory is <paramref name="factory"/>, as <c>WithUrl</c> registers its own.</summary>
    private static IHubConnectionBuilder Builder(IConnectionFactory factory)
    {
        var builder = new HubConnectionBuilder();
        builder.Services.AddSingleton(_ => factory);
        builder.Services.AddSingleton<EndPoint>(new IPEndPoint(IPAddress.Loopback, 0));
        return builder;
    }

    [Fact]
    public async Task A_close_message_in_the_servers_last_buffer_reaches_the_client()
    {
        await Frames.SendAsync(_client, Frames.Handshake);
        // The server answers, says Close and hangs up, all before the relay reads a byte of it
        await _upstream.ServerSendsAsync(HandshakeResponse + CloseMessage, thenHangUp: true);

        var relay = await SignalRRelay.ConnectAsync(_relaySide, Builder(_upstream));
        Assert.NotNull(relay);
        var running = relay.RunAsync();

        Assert.Equal(HandshakeResponse, (await Frames.ReceiveAsync(_client, Within)).Text);
        Assert.Equal(CloseMessage, (await Frames.ReceiveAsync(_client, Within)).Text);
        var close = await Frames.ReceiveAsync(_client, Within);
        Assert.Equal(WebSocketMessageType.Close, close.Type);
        await Frames.AnswerCloseAsync(_client);
        await running.WaitAsync(Within);
        await relay.DisposeAsync();
        Assert.True(_upstream.ConnectionDisposed);
        Assert.True(_upstream.Disposed);
    }

    [Fact]
    public async Task A_failed_upstream_connection_closes_the_client_socket_and_disposes_the_relays_services()
    {
        var refusing = new FakeUpstreamConnection
        {
            Refuse = new HttpRequestException("Response status code does not indicate success: 401 (Unauthorized).", null, HttpStatusCode.Unauthorized),
        };
        await Frames.SendAsync(_client, Frames.Handshake);

        var connecting = SignalRRelay.ConnectAsync(_relaySide, Builder(refusing));

        Assert.Equal("{\"error\":\"Failed to connect to upstream server (401 Unauthorized)\"}\x1e", (await Frames.ReceiveAsync(_client, Within)).Text);
        var close = await Frames.ReceiveAsync(_client, Within);
        Assert.Equal(WebSocketMessageType.Close, close.Type);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, close.CloseStatus);
        await Frames.AnswerCloseAsync(_client);
        Assert.Null(await connecting.WaitAsync(Within));
        Assert.True(refusing.Disposed);
    }

    [Fact]
    public async Task A_server_that_never_answers_the_handshake_gets_the_client_closed_after_the_timeout()
    {
        await Frames.SendAsync(_client, Frames.Handshake);

        var connecting = SignalRRelay.ConnectAsync(_relaySide, Builder(_upstream), handshakeTimeout: TimeSpan.FromMilliseconds(300));

        var close = await Frames.ReceiveAsync(_client, Within);
        Assert.Equal(WebSocketMessageType.Close, close.Type);
        Assert.Equal("No handshake response from upstream server", close.CloseReason);
        await Frames.AnswerCloseAsync(_client);
        Assert.Null(await connecting.WaitAsync(Within));
        Assert.True(_upstream.ConnectionDisposed);
        Assert.True(_upstream.Disposed);
    }
}
