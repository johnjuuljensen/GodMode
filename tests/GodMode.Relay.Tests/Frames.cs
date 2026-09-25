using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace GodMode.Relay.Tests;

/// <summary>A SignalR client at the frame level, for what a HubConnection hides: the handshake, and how the socket ends.</summary>
internal static class Frames
{
    public const string Handshake = "{\"protocol\":\"json\",\"version\":1}\x1e";

    public sealed record Frame(WebSocketMessageType Type, string Text, WebSocketCloseStatus? CloseStatus, string? CloseReason);

    public static Task SendAsync(WebSocket ws, string text) =>
        ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>The next whole frame; a <see cref="TimeoutException"/> when none comes within <paramref name="within"/>.</summary>
    public static async Task<Frame> ReceiveAsync(WebSocket ws, TimeSpan within)
    {
        using var timeout = new CancellationTokenSource(within);
        var buffer = new byte[8192];
        using var text = new MemoryStream();
        WebSocketReceiveResult result;
        try
        {
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                text.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"No frame within {within}");
        }
        return new Frame(result.MessageType, Encoding.UTF8.GetString(text.ToArray()), result.CloseStatus, result.CloseStatusDescription);
    }

    /// <summary>Answers a close frame, as a browser does by itself.</summary>
    public static Task AnswerCloseAsync(WebSocket ws) =>
        ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
}

internal static class WebSocketPair
{
    /// <summary>Both ends of one WebSocket over loopback TCP: the relay's, as the local server accepts it, and the WebView's.</summary>
    public static async Task<(WebSocket Relay, WebSocket Client)> CreateAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var client = new TcpClient();
            var connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var accepted = await listener.AcceptTcpClientAsync();
            await connecting;
            return (WebSocket.CreateFromStream(accepted.GetStream(), isServer: true, subProtocol: null, TimeSpan.Zero),
                WebSocket.CreateFromStream(client.GetStream(), isServer: false, subProtocol: null, TimeSpan.Zero));
        }
        finally
        {
            listener.Stop();
        }
    }
}
