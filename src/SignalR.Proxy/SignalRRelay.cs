using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace SignalR.Proxy;

/// <summary>
/// Accepts a WebSocket from a client, connects to a SignalR server via the builder's
/// IConnectionFactory (which handles negotiate + auth), and relays all messages
/// bidirectionally. Exposes a tee'd HubConnection for typed callback handling.
/// </summary>
public class SignalRRelay : IAsyncDisposable
{
    private const char RecordSeparator = '\x1e';

    private readonly string _connectionId;
    private readonly WebSocket _clientWs;
    private readonly ConnectionContext _serverConnection;
    private readonly IDuplexPipe _serverPipe;
    private readonly TeeConnection _tee;
    private readonly SemaphoreSlim _serverWriteLock = new(1, 1);
    private readonly Action<string> _log;

    public HubConnection HubConnection { get; }

    private SignalRRelay(
        string connectionId,
        WebSocket clientWs,
        ConnectionContext serverConnection,
        TeeConnection tee,
        HubConnection hubConnection,
        Action<string>? log)
    {
        _connectionId = connectionId;
        _clientWs = clientWs;
        _serverConnection = serverConnection;
        _serverPipe = serverConnection.Transport;
        _tee = tee;
        _log = log ?? Console.WriteLine;
        HubConnection = hubConnection;
    }

    /// <summary>
    /// Connects to a SignalR server and sets up the relay.
    /// </summary>
    /// <param name="clientWs">WebSocket accepted from the local client (e.g. React WebView).</param>
    /// <param name="serverBuilder">Pre-configured HubConnectionBuilder with URL and auth.
    /// The builder's IConnectionFactory handles negotiate + WebSocket upgrade.</param>
    /// <param name="log">Optional log callback.</param>
    public static async Task<SignalRRelay?> ConnectAsync(
        WebSocket clientWs,
        IHubConnectionBuilder serverBuilder,
        Action<string>? log = null)
    {
        var connectionId = Guid.NewGuid().ToString("N")[..8];
        var logFn = log ?? Console.WriteLine;

        logFn($"[{connectionId}] Client connected");

        // Step 1: Receive SignalR handshake from client
        var handshake = await ReceiveWsMessageAsync(clientWs);
        if (handshake == null)
        {
            logFn($"[{connectionId}] No handshake received");
            return null;
        }
        logFn($"[{connectionId}] Client handshake: {handshake.TrimEnd(RecordSeparator)}");

        // Step 2: Use the builder's factory to connect (handles negotiate + auth)
        var sp = serverBuilder.Services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IConnectionFactory>();
        var endpoint = sp.GetRequiredService<EndPoint>();

        ConnectionContext serverConnection;
        try
        {
            serverConnection = await factory.ConnectAsync(endpoint);
        }
        catch (Exception ex)
        {
            logFn($"[{connectionId}] Failed to connect to server: {ex.Message}");
            await SendWsMessageAsync(clientWs, "{\"error\":\"Failed to connect to upstream server\"}" + RecordSeparator);
            return null;
        }
        logFn($"[{connectionId}] Server connection established (negotiate complete)");

        var serverPipe = serverConnection.Transport;

        // Step 3: Forward SignalR protocol handshake through the pipe
        await serverPipe.Output.WriteAsync(Encoding.UTF8.GetBytes(handshake));
        await serverPipe.Output.FlushAsync();

        var response = await ReadPipeUntilSeparatorAsync(serverPipe.Input);
        if (response == null)
        {
            logFn($"[{connectionId}] No handshake response from server");
            if (serverConnection is IAsyncDisposable ad) await ad.DisposeAsync();
            return null;
        }
        await SendWsMessageAsync(clientWs, response);
        logFn($"[{connectionId}] Handshake complete, relaying messages");

        // Step 4: Create tee'd HubConnection for typed handlers
        var (tee, hubConnection) = await TeeConnection.CreateHubConnectionAsync();
        logFn($"[{connectionId}] Tee'd HubConnection ready");

        return new SignalRRelay(connectionId, clientWs, serverConnection, tee, hubConnection, log);
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
        await CloseWsGracefullyAsync(_clientWs);
        if (_serverConnection is IAsyncDisposable ad) await ad.DisposeAsync();
    }

    private async Task PumpClientToServerAsync(CancellationTokenSource cts)
    {
        var buffer = new byte[4096];
        try
        {
            while (_clientWs.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                var (message, result) = await ReceiveFullWsMessageAsync(_clientWs, buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;

                LogMessage(message, "C->S");
                await WriteToServerPipeAsync(message, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { _log($"[{_connectionId}] C->S error: {ex.Message}"); }
    }

    private async Task PumpServerToClientAsync(CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var result = await _serverPipe.Input.ReadAsync(cts.Token);
                if (result.IsCompleted || result.IsCanceled) break;

                var bytes = result.Buffer.ToArray();
                _serverPipe.Input.AdvanceTo(result.Buffer.End);
                if (bytes.Length == 0) continue;

                var segment = new ArraySegment<byte>(bytes);
                LogMessage(segment, "S->C");

                await _clientWs.SendAsync(segment, WebSocketMessageType.Text, true, cts.Token);

                await _tee.TeeWriter.WriteAsync(new ReadOnlyMemory<byte>(bytes), cts.Token);
                await _tee.TeeWriter.FlushAsync(cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"[{_connectionId}] S->C error: {ex.Message}"); }
    }

    private async Task PumpProxyToServerAsync(CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var result = await _tee.ProxyOutput.ReadAsync(cts.Token);
                if (result.IsCompleted) break;

                var bytes = result.Buffer.ToArray();
                _tee.ProxyOutput.AdvanceTo(result.Buffer.End);
                if (bytes.Length == 0) continue;

                LogMessage(new ArraySegment<byte>(bytes), "P->S");
                await WriteToServerPipeAsync(new ArraySegment<byte>(bytes), cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"[{_connectionId}] P->S error: {ex.Message}"); }
    }

    private async Task WriteToServerPipeAsync(ArraySegment<byte> data, CancellationToken ct)
    {
        await _serverWriteLock.WaitAsync(ct);
        try
        {
            await _serverPipe.Output.WriteAsync(
                new ReadOnlyMemory<byte>(data.Array!, data.Offset, data.Count), ct);
            await _serverPipe.Output.FlushAsync(ct);
        }
        finally { _serverWriteLock.Release(); }
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static async Task<string?> ReadPipeUntilSeparatorAsync(PipeReader reader)
    {
        while (true)
        {
            var result = await reader.ReadAsync();
            if (result.IsCompleted) return null;

            var buffer = result.Buffer;
            var position = buffer.PositionOf((byte)RecordSeparator);
            if (position != null)
            {
                var end = buffer.GetPosition(1, position.Value);
                var message = Encoding.UTF8.GetString(buffer.Slice(0, end).ToArray());
                reader.AdvanceTo(end);
                return message;
            }
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
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
                switch (typeProp.GetInt32())
                {
                    case 1: _log($"[{_connectionId}] {direction} Invoke: {root.GetProperty("target").GetString()}"); break;
                    case 3: _log($"[{_connectionId}] {direction} Completion: {root.GetProperty("invocationId").GetString()}"); break;
                    case 6: break; // Ping
                    default: _log($"[{_connectionId}] {direction} Type: {typeProp.GetInt32()}"); break;
                }
            }
            catch { /* not JSON or missing fields */ }
        }
    }

    private static async Task<(ArraySegment<byte> Message, WebSocketReceiveResult Result)> ReceiveFullWsMessageAsync(
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

    private static async Task<string?> ReceiveWsMessageAsync(WebSocket ws)
    {
        var buffer = new byte[4096];
        var (message, result) = await ReceiveFullWsMessageAsync(ws, buffer, CancellationToken.None);
        if (result.MessageType == WebSocketMessageType.Close) return null;
        return Encoding.UTF8.GetString(message.Array!, message.Offset, message.Count);
    }

    private static async Task SendWsMessageAsync(WebSocket ws, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task CloseWsGracefullyAsync(WebSocket ws)
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
        _serverWriteLock.Dispose();
    }
}
