using GodMode.ClientBase.Attention;
using GodMode.Shared.Enums;
using static GodMode.Relay.Tests.Attention;

namespace GodMode.Relay.Tests;

/// <summary>Each server's whole attention list, turned into notifications to show and to cancel.</summary>
public sealed class AttentionTrackerTests
{
    private readonly RecordingNotifier _notifier = new();
    private readonly AttentionTracker _tracker;

    public AttentionTrackerTests() => _tracker = new AttentionTracker(_notifier);

    [Fact]
    public void A_new_item_is_shown_once_however_often_the_list_repeats_it()
    {
        _tracker.Update("alpha", "Alpha", [Item("Default/root/a")]);
        _tracker.Update("alpha", "Alpha", [Item("Default/root/a")]);

        Assert.Equal(["show alpha:Default%2Froot%2Fa"], _notifier.Log);
    }

    [Fact]
    public void An_item_that_needs_something_else_is_shown_again()
    {
        _tracker.Update("alpha", "Alpha", [Item("Default/root/a", AttentionKind.Permission, "Run tests?")]);
        _tracker.Update("alpha", "Alpha", [Item("Default/root/a", AttentionKind.Question, "Which way?")]);

        Assert.Equal(2, _notifier.Log.Count);
        Assert.Equal(AttentionKind.Question, Assert.Single(_notifier.Shown.Values).Item.Kind);
    }

    [Fact]
    public void An_item_its_server_no_longer_lists_is_cancelled()
    {
        _tracker.Update("alpha", "Alpha", [Item("Default/root/a"), Item("Default/root/b")]);
        _tracker.Update("alpha", "Alpha", [Item("Default/root/b")]);

        Assert.Equal([("alpha", "Default/root/b")], _notifier.Items);
    }

    [Fact]
    public void The_same_project_id_on_two_servers_is_two_notifications_and_each_clears_alone()
    {
        _tracker.Update("alpha", "Alpha", [Item("Default/root/p")]);
        _tracker.Update("beta", "Beta", [Item("Default/root/p")]);
        Assert.Equal([("alpha", "Default/root/p"), ("beta", "Default/root/p")], _notifier.Items);

        _tracker.Update("alpha", "Alpha", []);
        Assert.Equal([("beta", "Default/root/p")], _notifier.Items);
    }

    [Fact]
    public void Removing_a_server_cancels_only_its_items()
    {
        _tracker.Update("alpha", "Alpha", [Item("Default/root/a")]);
        _tracker.Update("beta", "Beta", [Item("Default/root/b")]);

        _tracker.Remove("alpha");

        Assert.Equal([("beta", "Default/root/b")], _notifier.Items);
    }
}
