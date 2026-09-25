using System.Collections.Concurrent;

namespace GodMode.Server.Models;

/// <summary>
/// Pushes to hub clients that nobody waits on. Each is started at once, in the order it is sent,
/// and is then only watched. SignalR writes to one connection one message at a time, in the order
/// they were started, so every connection still gets them in order. A connection that stops reading
/// then holds up only its own messages, not the sender, until <see cref="Window"/> sends are
/// unfinished at once: from then on a send waits for the oldest one.
/// </summary>
public sealed class ClientSends
{
    /// <summary>How many sends may be unfinished before the next one waits.</summary>
    public const int Window = 256;

    private readonly ConcurrentQueue<Task> _unfinished = new();

    /// <summary>
    /// Starts <paramref name="send"/>. A failure goes to <paramref name="failed"/>, now or once it
    /// happens. Returns at once, unless <see cref="Window"/> sends are unfinished: then it returns
    /// the oldest.
    /// </summary>
    public Task SendAsync(Func<Task> send, Action<Exception> failed)
    {
        Task started;
        try { started = send(); }
        catch (Exception ex) { started = Task.FromException(ex); }
        var watched = WatchAsync(started, failed);
        if (watched.IsCompleted) return Task.CompletedTask;

        _unfinished.Enqueue(watched);
        while (_unfinished.TryPeek(out var oldest) && oldest.IsCompleted) _unfinished.TryDequeue(out _);
        return _unfinished.Count > Window && _unfinished.TryPeek(out var head) ? head : Task.CompletedTask;
    }

    private static async Task WatchAsync(Task started, Action<Exception> failed)
    {
        try { await started; }
        catch (Exception ex) { failed(ex); }
    }
}
