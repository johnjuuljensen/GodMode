using GodMode.HeadsetSpike;

namespace GodMode.HeadsetSpike.Tests;

/// <summary>
/// The classifier with the spike's defaults (400 ms gap, 800 ms long press), on key edges at given milliseconds:
/// press(at, held) is a down at <c>at</c> and an up <c>held</c> ms later.
/// </summary>
public class GestureClassifierTests
{
    private const int PlayPause = 0xB3, Next = 0xB0;

    private readonly GestureClassifier _classifier = new();
    private readonly List<Gesture> _gestures = [];

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private void Press(int at, int held = 100, int key = PlayPause)
    {
        _gestures.AddRange(_classifier.Feed(new KeyEdge(key, true, Ms(at))));
        _gestures.AddRange(_classifier.Feed(new KeyEdge(key, false, Ms(at + held))));
    }

    private void Flush(int at)
    {
        if (_classifier.Flush(Ms(at)) is { } gesture) _gestures.Add(gesture);
    }

    private IEnumerable<string> Names => _gestures.Select(g => g.Name);

    [Fact]
    public void One_press_is_a_single_once_the_gap_has_passed()
    {
        Press(0);
        Flush(300); // 200 ms after the release: another press could still join
        Assert.Empty(_gestures);
        Flush(501);
        Assert.Equal(["single"], Names);
        Assert.Equal(PlayPause, _gestures[0].Key);
    }

    [Fact]
    public void Presses_within_the_gap_are_a_double_and_a_triple()
    {
        Press(0);
        Press(300); // 200 ms after the first release
        Flush(800);
        Press(2000);
        Press(2300);
        Press(2600);
        Flush(3200);
        Assert.Equal(["double", "triple"], Names);
    }

    [Fact]
    public void A_press_after_the_gap_starts_a_new_gesture()
    {
        Press(0);
        Press(600); // 500 ms after the release, past the 400 ms gap
        Flush(1200);
        Assert.Equal(["single", "single"], Names);
    }

    [Fact]
    public void A_held_press_is_long_and_ends_at_its_release()
    {
        Press(0, held: 900);
        Assert.Equal(["long"], Names);
        Press(1000); // 100 ms after: a new gesture, not part of the long one
        Flush(1600);
        Assert.Equal(["long", "single"], Names);
    }

    [Fact]
    public void A_press_short_of_the_long_press_time_is_not_long()
    {
        Press(0, held: 700);
        Flush(1200);
        Assert.Equal(["single"], Names);
    }

    [Fact]
    public void A_double_press_ending_long_is_one_gesture()
    {
        Press(0);
        Press(300, held: 1000);
        Assert.Equal(["1x + long"], Names);
        Assert.Equal(2, _gestures[0].Presses);
    }

    [Fact]
    public void Auto_repeat_downs_are_one_press()
    {
        _gestures.AddRange(_classifier.Feed(new KeyEdge(PlayPause, true, Ms(0))));
        _gestures.AddRange(_classifier.Feed(new KeyEdge(PlayPause, true, Ms(500))));
        _gestures.AddRange(_classifier.Feed(new KeyEdge(PlayPause, true, Ms(530))));
        _gestures.AddRange(_classifier.Feed(new KeyEdge(PlayPause, false, Ms(1000))));
        Assert.Equal(["long"], Names);
    }

    [Fact]
    public void Another_key_ends_the_gesture()
    {
        Press(0);
        Press(200, key: Next);
        Flush(800);
        Assert.Equal([PlayPause, Next], _gestures.Select(g => g.Key));
        Assert.Equal(["single", "single"], Names);
    }

    [Fact]
    public void An_up_without_a_down_is_dropped()
    {
        _gestures.AddRange(_classifier.Feed(new KeyEdge(PlayPause, false, Ms(0))));
        Flush(1000);
        Assert.Empty(_gestures);
    }

    [Fact]
    public void Nothing_is_flushed_while_a_key_is_held()
    {
        _gestures.AddRange(_classifier.Feed(new KeyEdge(PlayPause, true, Ms(0))));
        Flush(5000);
        Assert.Empty(_gestures);
    }
}
