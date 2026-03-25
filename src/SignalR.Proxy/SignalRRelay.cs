using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ILogger _logger;

    public HubConnection HubConnection { get; }

    private SignalRRelay(
        string connectionId,
        WebSocket clientWs,
        ConnectionContext serverConnection,
        TeeConnection tee,
        HubConnection hubConnection,
        ILogger logger)
    {
        _connectionId = connectionId;
        _clientWs = clientWs;
        _serverConnection = serverConnection;
        _serverPipe = serverConnection.Transport;
        _tee = tee;
        _logger = logger;
        HubConnection = hubConnection;
    }

    /// <summary>
    /// Connects to a SignalR server and sets up the relay.
    /// </summary>
    /// <param name="clientWs">WebSocket accepted from the local client (e.g. React WebView).</param>
    /// <param name="serverBuilder">Pre-configured HubConnectionBuilder with URL and auth.
    /// The builder's IConnectionFactory handles negotiate + WebSocket upgrade.</param>
    /// <param name="logger">Optional logger.</param>
    public static async Task<SignalRRelay?> ConnectAsync(
        WebSocket clientWs,
        IHubConnectionBuilder serverBuilder,
        ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        var connectionId = Guid.NewGuid().ToString("N")[..8];

        log.LogInformation("[{ConnId}] Client WebSocket connected", connectionId);

        // Step 1: Receive SignalR handshake from client
        var handshake = await ReceiveWsMessageAsync(clientWs);
        if (handshake == null)
        {
            log.LogWarning("[{ConnId}] No handshake received from client", connectionId);
            return null;
        }
        log.LogDebug("[{ConnId}] Client handshake: {Handshake}", connectionId, handshake.TrimEnd(RecordSeparator));

        // Step 2: Use the builder's factory to connect (handles negotiate + auth)
        var sp = serverBuilder.Services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IConnectionFactory>();
        var endpoint = sp.GetRequiredService<EndPoint>();

        ConnectionContext serverConnection;
        try
        {
            log.LogDebug("[{ConnId}] Connecting to server endpoint...", connectionId);
            serverConnection = await factory.ConnectAsync(endpoint);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[{ConnId}] Failed to connect to server", connectionId);
            await SendWsMessageAsync(clientWs, "{\"error\":\"Failed to connect to upstream server\"}" + RecordSeparator);
            return null;
        }
        log.LogInformation("[{ConnId}] Server connection established (negotiate complete)", connectionId);

        var serverPipe = serverConnection.Transport;

        // Step 3: Forward SignalR protocol handshake through the pipe
        await serverPipe.Output.WriteAsync(Encoding.UTF8.GetBytes(handshake));
        await serverPipe.Output.FlushAsync();

        var response = await ReadPipeUntilSeparatorAsync(serverPipe.Input);
        if (response == null)
        {
            log.LogWarning("[{ConnId}] No handshake response from server", connectionId);
            if (serverConnection is IAsyncDisposable ad) await ad.DisposeAsync();
            return null;
        }
        await SendWsMessageAsync(clientWs, response);
        log.LogInformation("[{ConnId}] Handshake complete, relay active", connectionId);

        // Step 4: Create tee'd HubConnection for typed handlers
        var (tee, hubConnection) = await TeeConnection.CreateHubConnectionAsync();
        log.LogDebug("[{ConnId}] Tee'd HubConnection ready", connectionId);

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
        _logger.LogInformation("[{ConnId}] Relay closed", _connectionId);

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

                LogFrame(message, "C->S");
                await WriteToServerPipeAsync(message, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { _logger.LogWarning("[{ConnId}] C->S error: {Error}", _connectionId, ex.Message); }
    }

    private async Task PumpServerToClientAsync(CancellationTokenSource cts)
    {
        // Buffer partial SignalR messages — only forward complete ones (ending with \x1e)
        byte[] pending = [];
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var result = await _serverPipe.Input.ReadAsync(cts.Token);
                if (result.IsCompleted || result.IsCanceled) break;

                var newBytes = result.Buffer.ToArray();
                _serverPipe.Input.AdvanceTo(result.Buffer.End);
                if (newBytes.Length == 0) continue;

                // Combine with any pending partial data
                byte[] allBytes;
                if (pending.Length > 0)
                {
                    allBytes = new byte[pending.Length + newBytes.Length];
                    pending.CopyTo(allBytes, 0);
                    newBytes.CopyTo(allBytes, pending.Length);
                }
                else
                {
                    allBytes = newBytes;
                }

                // Find the last record separator — only send complete messages
                var lastSep = Array.LastIndexOf(allBytes, (byte)RecordSeparator);
                if (lastSep < 0)
                {
                    pending = allBytes;
                    _logger.LogTrace("[{ConnId}] S->C buffering {Len} bytes (no complete message yet)",
                        _connectionId, allBytes.Length);
                    continue;
                }

                var completeLen = lastSep + 1;
                var segment = new ArraySegment<byte>(allBytes, 0, completeLen);
                LogFrame(segment, "S->C");

                await _clientWs.SendAsync(segment, WebSocketMessageType.Text, true, cts.Token);

                await _tee.TeeWriter.WriteAsync(new ReadOnlyMemory<byte>(allBytes, 0, completeLen), cts.Token);
                await _tee.TeeWriter.FlushAsync(cts.Token);

                // Keep any remaining bytes for next iteration
                pending = completeLen < allBytes.Length ? allBytes[completeLen..] : [];
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning("[{ConnId}] S->C error: {Error}", _connectionId, ex.Message); }
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

                LogFrame(new ArraySegment<byte>(bytes), "P->S");
                await WriteToServerPipeAsync(new ArraySegment<byte>(bytes), cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning("[{ConnId}] P->S error: {Error}", _connectionId, ex.Message); }
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

    private void LogFrame(ArraySegment<byte> data, string direction)
    {
        if (!_logger.IsEnabled(LogLevel.Debug)) return;

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
                    case 1: // Invocation
                        var target = root.GetProperty("target").GetString();
                        _logger.LogDebug("[{ConnId}] {Dir} Invoke: {Target}", _connectionId, direction, target);
                        if (_logger.IsEnabled(LogLevel.Trace))
                            _logger.LogTrace("[{ConnId}] {Dir} Frame: {Raw}", _connectionId, direction, raw);
                        break;
                    case 3: // Completion
                        var invId = root.GetProperty("invocationId").GetString();
                        var hasError = root.TryGetProperty("error", out var errorProp);
                        if (hasError)
                        {
                            var errorMsg = errorProp.GetString();
                            _logger.LogWarning("[{ConnId}] {Dir} Completion ERROR: invId={InvId} error={Error}",
                                _connectionId, direction, invId, errorMsg);
                        }
                        else
                        {
                            _logger.LogDebug("[{ConnId}] {Dir} Completion: invId={InvId}", _connectionId, direction, invId);
                        }
                        if (_logger.IsEnabled(LogLevel.Trace))
                            _logger.LogTrace("[{ConnId}] {Dir} Frame: {Raw}", _connectionId, direction, raw);
                        break;
                    case 6: break; // Ping — suppress
                    default:
                        _logger.LogDebug("[{ConnId}] {Dir} Type={Type}", _connectionId, direction, typeProp.GetInt32());
                        break;
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
