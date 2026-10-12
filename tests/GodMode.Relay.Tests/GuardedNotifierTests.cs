using GodMode.ClientBase.Attention;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using static GodMode.Relay.Tests.Attention;

namespace GodMode.Relay.Tests;

/// <summary>
/// A notifier that fails on one item (Windows' toast builder refuses a payload over about 5 KB, #491) costs that item
/// alone: the tracker goes on showing and removing the others.
/// </summary>
public sealed class GuardedNotifierTests
{
    /// <summary>Refuses an item whose text is longer than a toast's payload holds, as AppNotificationBuilder does.</summary>
    private sealed class RefusingNotifier : IAttentionNotifier
    {
        public RecordingNotifier Recorded { get; } = new();

        public void Show(AttentionNotice notice)
        {
            if (notice.Item.Text.Length > 5000) throw new ArgumentException("Maximum payload size exceeded");
            Recorded.Show(notice);
        }

        public void Cancel(AttentionLink link) => Recorded.Cancel(link);

        public IReadOnlyCollection<AttentionLink> Showing() => Recorded.Showing();
    }

    private readonly RefusingNotifier _inner = new();
    private readonly AttentionTracker _tracker;

    public GuardedNotifierTests() => _tracker = new AttentionTracker(new GuardedNotifier(_inner, NullLogger.Instance));

    private static AttentionItem TooLong(string projectId) => Item(projectId, AttentionKind.Finished, new string('x', 6000));

    [Fact]
    public void An_item_the_notifier_refuses_does_not_throw_out_of_the_tracker()
    {
        var update = Record.Exception(() => _tracker.Update("alpha", "Alpha", [TooLong("Default/root/long"), Item("Default/root/b")]));

        Assert.Null(update);
        Assert.Equal([("alpha", "Default/root/b")], _inner.Recorded.Items);
    }

    [Fact]
    public void After_an_item_the_notifier_refused_the_next_is_shown_and_the_gone_are_removed()
    {
        _tracker.Update("alpha", "Alpha", [TooLong("Default/root/long"), Item("Default/root/b")]);

        _tracker.Update("alpha", "Alpha", [TooLong("Default/root/long"), Item("Default/root/c")]);

        Assert.Equal([("alpha", "Default/root/c")], _inner.Recorded.Items);
        Assert.Contains("cancel alpha:Default%2Froot%2Fb", _inner.Recorded.Log);
    }

    /// <summary>A toast's details are capped, so a long AskUserQuestion stays within the payload; a click opens it in full.</summary>
    [Fact]
    public void Details_of_many_long_questions_are_capped()
    {
        var questions = Enumerable.Range(1, 4).Select(i => new QuestionItem($"Question {i}?", $"Q{i}",
            [new($"Option {i}a", new string('a', 1500)), new($"Option {i}b", new string('b', 1500))], false)).ToList();
        var item = Item("Default/root/q") with { Question = new PendingQuestion("r1", questions, DateTime.UtcNow) };

        var details = AttentionNotificationText.Details(item)!;

        Assert.Equal(AttentionNotificationText.MaxDetailsLength, details.Length);
        Assert.EndsWith("…", details);
        Assert.StartsWith(AttentionNotificationText.Body(item)[..100], details);
    }
}
