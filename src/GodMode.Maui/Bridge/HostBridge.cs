using System.Collections.Concurrent;
using System.Text.Json;
using GodMode.Shared;

namespace GodMode.Maui.Bridge;

/// <summary>
/// Manages bidirectional communication between the MAUI host and the React app
/// via HybridWebView's raw message channel.
///
/// Supports three patterns:
/// - Fire-and-forget: Send(type, payload) — one-way notification
/// - Request/response: RequestAsync — send with correlation ID, await response
/// - Events: MessageReceived — subscribe to messages from React
/// </summary>
public class HostBridge
{
    private readonly HybridWebView _webView;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>> _pending = new();

    /// <summary>
    /// Raised when a message is received from the React app that is not a response to a pending request.
    /// </summary>
    public event Action<BridgeMessage>? MessageReceived;

    public HostBridge(HybridWebView webView)
    {
        _webView = webView;
    }

    /// <summary>
    /// Send a fire-and-forget message to the React app.
    /// </summary>
    public void Send(string type, object? payload = null)
    {
        var message = new BridgeMessage(type, Payload: Serialize(payload));
        SendRaw(message);
    }

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

        var message = new BridgeMessage(type, id, Serialize(payload));
        SendRaw(message);

        var result = await tcs.Task;
        return result.HasValue
            ? result.Value.Deserialize<T>(JsonDefaults.Options)
            : default;
    }

    /// <summary>
    /// Called by MainPage when a raw message arrives from JavaScript.
    /// Routes responses to pending requests, raises MessageReceived for everything else.
    /// </summary>
    public void HandleMessageFromJs(string rawJson)
    {
        BridgeMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<BridgeMessage>(rawJson, JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HostBridge] Failed to deserialize message: {ex.Message}");
            return;
        }

        if (message is null) return;

        // If this is a response to a pending request, complete the TCS
        if (message.Id is not null && _pending.TryRemove(message.Id, out var tcs))
        {
            tcs.TrySetResult(message.Payload);
            return;
        }

        // Otherwise raise as an event
        MessageReceived?.Invoke(message);
    }

    private void SendRaw(BridgeMessage message)
    {
        var json = JsonSerializer.Serialize(message, JsonDefaults.Compact);
        _webView.SendRawMessage(json);
    }

    private static JsonElement? Serialize(object? value) =>
        value is null
            ? null
            : JsonSerializer.SerializeToElement(value, JsonDefaults.Options);
}
