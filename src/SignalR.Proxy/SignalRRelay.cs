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
/// bidirectionally. Frames are forwarded as-is, so the relay needs no knowledge of the hub contract.
/// However a relay ends, the client gets a close frame with the reason (unless its socket is gone already).
/// </summary>
public class SignalRRelay : IAsyncDisposable
{
    private const char RecordSeparator = '\x1e';

    /// <summary>How long the client has to send its handshake, and the server to answer it.</summary>
    public static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a client has to answer the relay's close frame before its socket is dropped.</summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly string _connectionId;
    private readonly WebSocket _clientWs;
    private readonly ServiceProvider _services;
    private readonly ConnectionContext _serverConnection;
    private readonly IDuplexPipe _serverPipe;
    private readonly SemaphoreSlim _serverWriteLock = new(1, 1);
    private readonly ILogger _logger;

    private SignalRRelay(
        string connectionId,
        WebSocket clientWs,
        ServiceProvider services,
        ConnectionContext serverConnection,
        ILogger logger)
    {
        _connectionId = connectionId;
        _clientWs = clientWs;
        _services = services;
        _serverConnection = serverConnection;
        _serverPipe = serverConnection.Transport;
        _logger = logger;
    }

    /// <summary>
    /// Connects to a SignalR server and sets up the relay. On any failure the client socket is closed with the reason
    /// and null is returned.
    /// </summary>
    /// <param name="clientWs">WebSocket accepted from the local client (e.g. React WebView).</param>
    /// <param name="serverBuilder">Pre-configured HubConnectionBuilder with URL and auth.
    /// The builder's IConnectionFactory handles negotiate + WebSocket upgrade.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="handshakeTimeout">How long the client may take to send its handshake, and the server to answer it
    /// (<see cref="DefaultHandshakeTimeout"/> when null).</param>
    /// <param name="ct">Ends the attempt, closing the client socket.</param>
    public static async Task<SignalRRelay?> ConnectAsync(
        WebSocket clientWs,
        IHubConnectionBuilder serverBuilder,
        ILogger? logger = null,
        TimeSpan? handshakeTimeout = null,
        CancellationToken ct = default)
    {
        var log = logger ?? NullLogger.Instance;
        var timeout = handshakeTimeout ?? DefaultHandshakeTimeout;
        var connectionId = Guid.NewGuid().ToString("N")[..8];

        log.LogInformation("[{ConnId}] Client WebSocket connected", connectionId);

        // Step 1: Receive SignalR handshake from client. Bounded: until the relay runs, nothing else ends a quiet client
        var receiving = ReceiveWsMessageAsync(clientWs);
        var handshake = await WithinAsync(receiving, timeout, ct);
        if (handshake == null)
        {
            log.LogWarning("[{ConnId}] No handshake received from client", connectionId);
            await CloseClientAsync(clientWs, ct.IsCancellationRequested
                ? (WebSocketCloseStatus.EndpointUnavailable, "Relay dropped")
                : (WebSocketCloseStatus.PolicyViolation, "No handshake received"), receiving);
            return null;
        }
        log.LogDebug("[{ConnId}] Client handshake: {Handshake}", connectionId, handshake.TrimEnd(RecordSeparator));

        // Step 2: Use the builder's factory to connect (handles negotiate + auth). The relay owns this provider
        ServiceProvider? services = serverBuilder.Services.BuildServiceProvider();
        ConnectionContext? serverConnection = null;
        try
        {
            try
            {
                log.LogDebug("[{ConnId}] Connecting to server endpoint...", connectionId);
                serverConnection = await services.GetRequiredService<IConnectionFactory>()
                    .ConnectAsync(services.GetRequiredService<EndPoint>(), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // A wrong key: 401 on negotiate
                var status = ex is HttpRequestException { StatusCode: { } code } ? $" ({(int)code} {code})" : "";
                log.LogWarning("[{ConnId}] Failed to connect to server{Status}: {Error}", connectionId, status, ex.Message);
                await SendWsMessageAsync(clientWs, $"{{\"error\":\"Failed to connect to upstream server{status}\"}}{RecordSeparator}");
                await CloseClientAsync(clientWs, (WebSocketCloseStatus.EndpointUnavailable, $"Upstream server unavailable{status}"));
                return null;
            }
            log.LogInformation("[{ConnId}] Server connection established (negotiate complete)", connectionId);

            var serverPipe = serverConnection.Transport;

            // Step 3: Forward SignalR protocol handshake through the pipe
            await serverPipe.Output.WriteAsync(Encoding.UTF8.GetBytes(handshake), ct);
            await serverPipe.Output.FlushAsync(ct);

            var response = await WithinAsync(ReadPipeUntilSeparatorAsync(serverPipe.Input, ct), timeout, ct);
            if (response == null)
            {
                log.LogWarning("[{ConnId}] No handshake response from server", connectionId);
                await CloseClientAsync(clientWs, (WebSocketCloseStatus.EndpointUnavailable, "No handshake response from upstream server"));
                return null;
            }
            await SendWsMessageAsync(clientWs, response);
            log.LogInformation("[{ConnId}] Handshake complete, relay active", connectionId);

            var relay = new SignalRRelay(connectionId, clientWs, services, serverConnection, log);
            (services, serverConnection) = (null, null);
            return relay;
        }
        catch (Exception ex)
        {
            // The client went away mid-handshake, or the attempt was ended (DropAllRelays, shutdown)
            log.LogWarning("[{ConnId}] Relay setup ended: {Error}", connectionId, ex.Message);
            await CloseClientAsync(clientWs, (WebSocketCloseStatus.EndpointUnavailable, "Relay dropped"));
            return null;
        }
        finally
        {
            if (serverConnection is IAsyncDisposable connection) await connection.DisposeAsync();
            if (services != null) await services.DisposeAsync();
        }
    }

    /// <summary>
    /// Runs the relay pumps until the client or server disconnects, or <paramref name="ct"/> ends the relay.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var clientToServer = PumpClientToServerAsync();
        var serverToClient = PumpServerToClientAsync(stop.Token);

        var ended = await Task.WhenAny(clientToServer, serverToClient);
        await stop.CancelAsync();
        await serverToClient;

        // A close frame after whatever the server sent last, rather than aborting the socket, which can lose it
        (WebSocketCloseStatus Status, string Reason) close = ended == clientToServer ? (WebSocketCloseStatus.NormalClosure, "")
            : ct.IsCancellationRequested ? (WebSocketCloseStatus.EndpointUnavailable, "Relay dropped")
            : (WebSocketCloseStatus.EndpointUnavailable, "Upstream server closed the connection");
        _logger.LogInformation("[{ConnId}] Relay closed{Reason}", _connectionId, close.Reason is "" ? "" : $": {close.Reason}");

        await CloseClientAsync(_clientWs, close, clientToServer);
        if (_serverConnection is IAsyncDisposable ad) await ad.DisposeAsync();
    }

    /// <summary>Until the client closes or its socket fails. Not cancelled: the relay ends it by closing the client.</summary>
    private async Task PumpClientToServerAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var (message, result) = await ReceiveFullWsMessageAsync(_clientWs, buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) return;

                LogFrame(message, "C->S");
                await WriteToServerPipeAsync(message);
            }
        }
        catch (Exception ex) { _logger.LogDebug("[{ConnId}] C->S ended: {Error}", _connectionId, ex.Message); }
    }

    private async Task PumpServerToClientAsync(CancellationToken ct)
    {
        var input = _serverPipe.Input;
        try
        {
            while (true)
            {
                var result = await input.ReadAsync(ct);
                var buffer = result.Buffer;

                // Only complete SignalR messages (ending with \x1e) go to the client; a partial one waits in the pipe
                var complete = CompleteMessages(buffer);
                if (!complete.IsEmpty)
                {
                    var segment = new ArraySegment<byte>(complete.ToArray());
                    LogFrame(segment, "S->C");
                    await _clientWs.SendAsync(segment, WebSocketMessageType.Text, true, ct);
                }
                input.AdvanceTo(complete.End, buffer.End);

                // After forwarding: the server's Close message often comes in the same read as the end of the stream
                if (result.IsCompleted || result.IsCanceled) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning("[{ConnId}] S->C error: {Error}", _connectionId, ex.Message); }
    }

    private async Task WriteToServerPipeAsync(ArraySegment<byte> data)
    {
        await _serverWriteLock.WaitAsync();
        try
        {
            await _serverPipe.Output.WriteAsync(new ReadOnlyMemory<byte>(data.Array!, data.Offset, data.Count));
            await _serverPipe.Output.FlushAsync();
        }
        finally { _serverWriteLock.Release(); }
    }

    // ── Helpers ───────────────────────────────────────────────────

    /// <summary>The buffer up to and including its last record separator: the whole messages in it.</summary>
    private static ReadOnlySequence<byte> CompleteMessages(ReadOnlySequence<byte> buffer)
    {
        var reader = new SequenceReader<byte>(buffer);
        var end = buffer.Start;
        while (reader.TryAdvanceTo((byte)RecordSeparator))
            end = reader.Position;
        return buffer.Slice(buffer.Start, end);
    }

    /// <summary>The first message in the pipe, or null when the stream ends without one.</summary>
    private static async Task<string?> ReadPipeUntilSeparatorAsync(PipeReader reader, CancellationToken ct)
    {
        while (true)
        {
            var result = await reader.ReadAsync(ct);
            var buffer = result.Buffer;
            // Looked for before the end of the stream: the server may answer and hang up in one read
            var position = buffer.PositionOf((byte)RecordSeparator);
            if (position != null)
            {
                var end = buffer.GetPosition(1, position.Value);
                var message = Encoding.UTF8.GetString(buffer.Slice(0, end).ToArray());
                reader.AdvanceTo(end);
                return message;
            }
            if (result.IsCompleted) return null;
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>The task's result, or null when it takes longer than <paramref name="timeout"/> or <paramref name="ct"/> ends it.</summary>
    private static async Task<string?> WithinAsync(Task<string?> task, TimeSpan timeout, CancellationToken ct)
    {
        try { return await task.WaitAsync(timeout, ct); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { return null; }
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

    /// <summary>One whole message from the client, or null when it closes or its socket fails first.</summary>
    private static async Task<string?> ReceiveWsMessageAsync(WebSocket ws)
    {
        try
        {
            var (message, result) = await ReceiveFullWsMessageAsync(ws, new byte[4096], CancellationToken.None);
            return result.MessageType == WebSocketMessageType.Close ? null : Encoding.UTF8.GetString(message.AsSpan());
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { return null; }
    }

    private static async Task SendWsMessageAsync(WebSocket ws, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    /// <summary>
    /// Sends the client a close frame with the reason, waits briefly for its answer, and disposes the socket.
    /// <paramref name="receiving"/> is a receive already pending on the socket, which takes the answer.
    /// </summary>
    private static async Task CloseClientAsync(WebSocket ws, (WebSocketCloseStatus Status, string Reason) close, Task? receiving = null)
    {
        try
        {
            using var timeout = new CancellationTokenSource(CloseTimeout);
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                if (receiving == null)
                {
                    await ws.CloseAsync(close.Status, close.Reason, timeout.Token);
                }
                else
                {
                    await ws.CloseOutputAsync(close.Status, close.Reason, timeout.Token);
                    await receiving.WaitAsync(timeout.Token);
                }
            }
        }
        catch { /* best effort: a client that does not answer is dropped */ }
        ws.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _serverWriteLock.Dispose();
        await _services.DisposeAsync();
    }
}
