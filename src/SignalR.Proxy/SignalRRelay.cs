using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace SignalR.Proxy;

/// <summary>
/// Accepts a WebSocket from a client, connects to a SignalR server via raw WebSocket,
/// and relays all messages bidirectionally. Exposes a tee'd HubConnection for
/// typed callback handling (Register&lt;T&gt;) and server invocations (CreateHubProxy&lt;T&gt;).
/// </summary>
public class SignalRRelay : IAsyncDisposable
{
    private const char RecordSeparator = '\x1e';

    private readonly string _connectionId;
    private readonly WebSocket _clientWs;
    private readonly WebSocket _serverWs;
    private readonly TeeConnection _tee;
    private readonly Action<string> _log;

    /// <summary>
    /// A HubConnection tee'd into the S→C message stream.
    /// Use TypedSignalR Register&lt;T&gt;() to handle server callbacks.
    /// </summary>
    public HubConnection HubConnection { get; }

    private SignalRRelay(
        string connectionId,
        WebSocket clientWs,
        WebSocket serverWs,
        TeeConnection tee,
        HubConnection hubConnection,
        Action<string>? log)
    {
        _connectionId = connectionId;
        _clientWs = clientWs;
        _serverWs = serverWs;
        _tee = tee;
        _log = log ?? Console.WriteLine;
        HubConnection = hubConnection;
    }

    /// <summary>
    /// Connects to a SignalR server and sets up the relay.
    /// </summary>
    /// <param name="clientWs">WebSocket accepted from the local client (e.g. React WebView).</param>
    /// <param name="serverUrl">SignalR hub URL (http/https — converted to ws/wss internally).</param>
    /// <param name="configureServerWs">Optional callback to configure the server WebSocket (e.g. auth headers).</param>
    /// <param name="log">Optional log callback.</param>
    public static async Task<SignalRRelay?> ConnectAsync(
        WebSocket clientWs,
        string serverUrl,
        Action<ClientWebSocket>? configureServerWs = null,
        Action<string>? log = null)
    {
        var connectionId = Guid.NewGuid().ToString("N")[..8];
        var logFn = log ?? Console.WriteLine;

        logFn($"[{connectionId}] Client connected");

        // Step 1: Receive SignalR handshake from client
        var handshake = await ReceiveMessageAsync(clientWs);
        if (handshake == null)
        {
            logFn($"[{connectionId}] No handshake received");
            return null;
        }
        logFn($"[{connectionId}] Client handshake: {handshake.TrimEnd(RecordSeparator)}");

        // Step 2: Connect to the real server via raw WebSocket
        var serverWs = new ClientWebSocket();
        configureServerWs?.Invoke(serverWs);
        var serverWsUrl = serverUrl.Replace("http://", "ws://").Replace("https://", "wss://");
        try
        {
            await serverWs.ConnectAsync(new Uri(serverWsUrl), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logFn($"[{connectionId}] Failed to connect to server: {ex.Message}");
            await SendMessageAsync(clientWs, "{\"error\":\"Failed to connect to upstream server\"}" + RecordSeparator);
            return null;
        }

        // Step 3: Forward handshake to server, relay response back
        await SendMessageAsync(serverWs, handshake);
        var response = await ReceiveMessageAsync(serverWs);
        if (response == null)
        {
            logFn($"[{connectionId}] No handshake response from server");
            serverWs.Dispose();
            return null;
        }
        await SendMessageAsync(clientWs, response);
        logFn($"[{connectionId}] Handshake complete, relaying messages");

        // Step 4: Create tee'd HubConnection
        var (tee, hubConnection) = await TeeConnection.CreateHubConnectionAsync();

        logFn($"[{connectionId}] Tee'd HubConnection ready");

        return new SignalRRelay(connectionId, clientWs, serverWs, tee, hubConnection, log);
    }

    /// <summary>
    /// Runs the relay pumps until the client or server disconnects.
    /// </summary>
    public async Task RunAsync()
    {
        using var cts = new CancellationTokenSource();

        var clientToServer = PumpClientToServerAsync(cts);
        var serverToClient = PumpServerToClientAsync(cts);
        var proxyToServer = PumpProxyToServerAsync(cts);

        try { await Task.WhenAny(clientToServer, serverToClient); }
        catch { /* one side closed */ }

        await cts.CancelAsync();
        _log($"[{_connectionId}] Connection closed");

        try { await HubConnection.StopAsync(); } catch { /* best effort */ }
        await _tee.DisposeAsync();
        await CloseGracefullyAsync(_clientWs);
        await CloseGracefullyAsync(_serverWs);
    }

    private async Task PumpClientToServerAsync(CancellationTokenSource cts)
    {
        var buffer = new byte[4096];
        try
        {
            while (_clientWs.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                var (message, result) = await ReceiveFullMessageAsync(_clientWs, buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;

                LogMessage(message, "C->S");
                await _serverWs.SendAsync(message, result.MessageType, true, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { _log($"[{_connectionId}] C->S error: {ex.Message}"); }
    }

    private async Task PumpServerToClientAsync(CancellationTokenSource cts)
    {
        var buffer = new byte[4096];
        try
        {
            while (_serverWs.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                var (message, result) = await ReceiveFullMessageAsync(_serverWs, buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;

                LogMessage(message, "S->C");

                // Forward to client
                await _clientWs.SendAsync(message, result.MessageType, true, cts.Token);

                // Tee into HubConnection
                await _tee.TeeWriter.WriteAsync(message, cts.Token);
                await _tee.TeeWriter.FlushAsync(cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { _log($"[{_connectionId}] S->C error: {ex.Message}"); }
    }

    private async Task PumpProxyToServerAsync(CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var result = await _tee.ProxyOutput.ReadAsync(cts.Token);
                if (result.IsCompleted) break;

                foreach (var segment in result.Buffer)
                {
                    if (segment.Length > 0)
                    {
                        var bytes = segment.ToArray();
                        LogMessage(new ArraySegment<byte>(bytes), "P->S");
                        await _serverWs.SendAsync(
                            new ArraySegment<byte>(bytes),
                            WebSocketMessageType.Text, true, cts.Token);
                    }
                }

                _tee.ProxyOutput.AdvanceTo(result.Buffer.End);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { _log($"[{_connectionId}] P->S error: {ex.Message}"); }
    }

    private void LogMessage(ArraySegment<byte> data, string direction)
    {
        var text = Encoding.UTF8.GetString(data.Array!, data.Offset, data.Count);
        foreach (var raw in text.Split(RecordSeparator))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) continue;
                var type = typeProp.GetInt32();
                switch (type)
                {
                    case 1:
                        _log($"[{_connectionId}] {direction} Invoke: {root.GetProperty("target").GetString()}");
                        break;
                    case 3:
                        _log($"[{_connectionId}] {direction} Completion: {root.GetProperty("invocationId").GetString()}");
                        break;
                    case 6: break; // Ping
                    default:
                        _log($"[{_connectionId}] {direction} Type: {type}");
                        break;
                }
            }
            catch { /* not JSON or missing fields — skip */ }
        }
    }

    private static async Task<(ArraySegment<byte> Message, WebSocketReceiveResult Result)> ReceiveFullMessageAsync(
        WebSocket ws, byte[] buffer, CancellationToken ct)
    {
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
        if (result.EndOfMessage)
            return (new ArraySegment<byte>(buffer, 0, result.Count), result);

        using var ms = new MemoryStream();
        ms.Write(buffer, 0, result.Count);
        while (!result.EndOfMessage)
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            ms.Write(buffer, 0, result.Count);
        }
        return (new ArraySegment<byte>(ms.ToArray()), result);
    }

    private static async Task<string?> ReceiveMessageAsync(WebSocket ws)
    {
        var buffer = new byte[4096];
        var (message, result) = await ReceiveFullMessageAsync(ws, buffer, CancellationToken.None);
        if (result.MessageType == WebSocketMessageType.Close) return null;
        return Encoding.UTF8.GetString(message.Array!, message.Offset, message.Count);
    }

    private static async Task SendMessageAsync(WebSocket ws, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task CloseGracefullyAsync(WebSocket ws)
    {
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        catch { /* best effort */ }
        ws.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        try { await HubConnection.DisposeAsync(); } catch { /* best effort */ }
        await _tee.DisposeAsync();
    }
}
