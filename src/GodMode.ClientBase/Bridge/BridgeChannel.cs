using System.Collections.Concurrent;
using System.Text.Json;
using GodMode.Shared;
using Microsoft.Extensions.Logging;

namespace GodMode.ClientBase.Bridge;

/// <summary>
/// Bidirectional communication between the app's host and the React app over a WebView's raw message channel.
/// The platform side (GodMode.Maui's HostBridge) supplies the page: its address, how to post to it, and its UI thread.
///
/// Supports four patterns:
/// - Fire-and-forget: Send(type, payload) — one-way notification
/// - Host request/response: RequestAsync — send with correlation ID, await React's response
/// - React request/response: Handle(type, handler) — React sends with a correlation ID, the handler's result
///   (or its exception message, as Error) goes back under the same ID and type
/// - Events: MessageReceived — any other message from React
///
/// Only the app's own page (<see cref="AppOrigin"/>) takes part. A message from any other page the WebView shows
/// reaches no handler and gets no reply, and nothing is sent to such a page.
/// </summary>
public abstract class BridgeChannel(ILogger logger)
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly ConcurrentDictionary<string, Func<JsonElement?, Task<object?>>> _handlers = new();

    /// <summary>
    /// Raised when a message is received from the React app that is neither a response nor a handled request.
    /// </summary>
    public event Action<BridgeMessage>? MessageReceived;

    /// <summary>The address of the page the WebView shows now, or null before it shows one. Read on the UI thread.</summary>
    protected abstract string? PageAddress { get; }

    /// <summary>Hands a raw message to the page's script. Called on the UI thread.</summary>
    protected abstract void PostToPage(string message);

    /// <summary>Runs an action on the UI thread: at once when called on it.</summary>
    protected abstract void OnUiThread(Action action);

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

    /// <summary>A raw message from the page's script, on any thread.</summary>
    protected void Receive(string rawJson) =>
        OnUiThread(() =>
        {
            if (OnAppPage("a message"))
                HandleMessageFromJs(rawJson);
        });

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
            logger.LogWarning("Failed to deserialize bridge message: {Error}", ex.Message);
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
            logger.LogWarning(ex, "Bridge request {Type} failed", request.Type);
            response = new BridgeMessage(request.Type, request.Id, Error: ex.Message);
        }
        SendRaw(response);
    }

    private void SendRaw(BridgeMessage message)
    {
        var json = JsonSerializer.Serialize(message, JsonDefaults.Compact);
        OnUiThread(() =>
        {
            if (OnAppPage(message.Type))
                PostToPage(json);
        });
    }

    /// <summary>
    /// The WebView shows one of the app's own pages. The shell keeps it there, so anything else is a page that
    /// got past that: it must not read the relay's secret or change the servers.
    /// </summary>
    private bool OnAppPage(string what)
    {
        var address = PageAddress;
        if (AppOrigin.Contains(address)) return true;
        logger.LogWarning("Bridge dropped {What}: the WebView shows {Origin}, not the app", what, AppOrigin.Of(address) ?? "no page");
        return false;
    }

    private static JsonElement? Serialize(object? value) =>
        value is null
            ? null
            : JsonSerializer.SerializeToElement(value, JsonDefaults.Options);
}
