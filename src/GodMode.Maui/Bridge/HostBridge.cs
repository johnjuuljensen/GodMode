using System.Collections.Concurrent;
using System.Text.Json;
using GodMode.Shared;
using Microsoft.Extensions.Logging;

namespace GodMode.Maui.Bridge;

/// <summary>
/// Manages bidirectional communication between the MAUI host and the React app
/// via HybridWebView's raw message channel.
///
/// Supports four patterns:
/// - Fire-and-forget: Send(type, payload) — one-way notification
/// - Host request/response: RequestAsync — send with correlation ID, await React's response
/// - React request/response: Handle(type, handler) — React sends with a correlation ID, the handler's result
///   (or its exception message, as Error) goes back under the same ID and type
/// - Events: MessageReceived — any other message from React
/// </summary>
public class HostBridge
{
    private readonly HybridWebView _webView;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly ConcurrentDictionary<string, Func<JsonElement?, Task<object?>>> _handlers = new();

    /// <summary>
    /// Raised when a message is received from the React app that is neither a response nor a handled request.
    /// </summary>
    public event Action<BridgeMessage>? MessageReceived;

    public HostBridge(HybridWebView webView, ILogger logger)
    {
        _webView = webView;
        _logger = logger;
        _webView.RawMessageReceived += (_, e) =>
        {
            if (e.Message is { } message)
                HandleMessageFromJs(message);
        };
    }

    /// <summary>Answers React's requests of one type with the handler's result.</summary>
    public void Handle<TRequest, TResponse>(string type, Func<TRequest, Task<TResponse>> handler) =>
        _handlers[type] = async payload =>
        {
            var request = payload.HasValue ? payload.Value.Deserialize<TRequest>(JsonDefaults.Options) : default;
            return await handler(request ?? throw new ArgumentException($"{type} needs a payload"));
        };

    /// <summary>Answers React's requests of one type, which carry no payload, with the handler's result.</summary>
    public void Handle<TResponse>(string type, Func<Task<TResponse>> handler) =>
        _handlers[type] = async _ => await handler();

    /// <summary>
    /// Send a fire-and-forget message to the React app.
    /// </summary>
    public void Send(string type, object? payload = null) =>
        SendRaw(new BridgeMessage(type, Payload: Serialize(payload)));

    /// <summary>
    /// Send a request to the React app and await a response.
    /// </summary>
    public async Task<T?> RequestAsync<T>(string type, object? payload = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement?>();
        _pending[id] = tcs;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));

        await using var reg = cts.Token.Register(() =>
        {
            _pending.TryRemove(id, out _);
            tcs.TrySetCanceled(ct);
        });

        SendRaw(new BridgeMessage(type, id, Serialize(payload)));

        var result = await tcs.Task;
        return result.HasValue
            ? result.Value.Deserialize<T>(JsonDefaults.Options)
            : default;
    }

    /// <summary>
    /// Routes responses to pending requests and requests to their handlers, and raises MessageReceived for everything else.
    /// </summary>
    private void HandleMessageFromJs(string rawJson)
    {
        BridgeMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<BridgeMessage>(rawJson, JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Failed to deserialize bridge message: {Error}", ex.Message);
            return;
        }

        if (message is null) return;

        if (message.Id is not null && _pending.TryRemove(message.Id, out var tcs))
            tcs.TrySetResult(message.Payload);
        else if (message.Id is not null && _handlers.TryGetValue(message.Type, out var handler))
            _ = AnswerAsync(message, handler);
        else
            MessageReceived?.Invoke(message);
    }

    private async Task AnswerAsync(BridgeMessage request, Func<JsonElement?, Task<object?>> handler)
    {
        BridgeMessage response;
        try
        {
            response = new BridgeMessage(request.Type, request.Id, Serialize(await handler(request.Payload)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bridge request {Type} failed", request.Type);
            response = new BridgeMessage(request.Type, request.Id, Error: ex.Message);
        }
        SendRaw(response);
    }

    private void SendRaw(BridgeMessage message)
    {
        var json = JsonSerializer.Serialize(message, JsonDefaults.Compact);
        MainThread.BeginInvokeOnMainThread(() => _webView.SendRawMessage(json));
    }

    private static JsonElement? Serialize(object? value) =>
        value is null
            ? null
            : JsonSerializer.SerializeToElement(value, JsonDefaults.Options);
}
