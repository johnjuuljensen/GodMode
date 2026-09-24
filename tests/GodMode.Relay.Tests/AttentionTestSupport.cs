using System.Collections.Concurrent;
using GodMode.ClientBase.Attention;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Relay.Tests;

/// <summary>
/// An <see cref="IAttentionNotifier"/> that keeps what is shown by key, as Android keeps notifications by tag:
/// a second Show under a key replaces the first.
/// </summary>
internal sealed class RecordingNotifier : IAttentionNotifier
{
    public ConcurrentDictionary<string, AttentionNotice> Shown { get; } = new();
    public ConcurrentQueue<string> Log { get; } = new();

    public void Show(AttentionNotice notice)
    {
        Shown[notice.Link.Key] = notice;
        Log.Enqueue($"show {notice.Link.Key}");
    }

    public void Cancel(AttentionLink link)
    {
        Shown.TryRemove(link.Key, out _);
        Log.Enqueue($"cancel {link.Key}");
    }

    /// <summary>Links an earlier run of the app left showing, before any tracker saw them.</summary>
    public List<AttentionLink> LeftShowing { get; } = [];

    public IReadOnlyCollection<AttentionLink> Showing() => [.. LeftShowing, .. Shown.Values.Select(n => n.Link)];

    /// <summary>What is shown, as (server, project) pairs.</summary>
    public IReadOnlyList<(string ServerId, string ProjectId)> Items =>
        [.. Shown.Values.Select(n => (n.Link.ServerId, n.Link.ProjectId)).Order()];
}

internal static class Attention
{
    public static AttentionItem Item(string projectId, AttentionKind kind = AttentionKind.Question, string text = "Which way?") =>
        new(projectId, projectId.Split('/')[^1], "Default", "root", kind, new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc), text);

    /// <summary>Waits up to 10 s for the condition.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}");
            await Task.Delay(20);
        }
    }
}
