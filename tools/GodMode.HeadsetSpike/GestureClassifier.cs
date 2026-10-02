namespace GodMode.HeadsetSpike;

/// <summary>One key going down or up, at a time on one clock (the log's stopwatch).</summary>
public readonly record struct KeyEdge(int Key, bool Down, TimeSpan At);

/// <summary>
/// Presses of one key read as one gesture: <paramref name="Presses"/> 1, 2, 3 for a single, double, triple press,
/// <paramref name="Long"/> when its last press was held for the long-press time.
/// </summary>
public sealed record Gesture(int Key, int Presses, bool Long, TimeSpan Start, TimeSpan End)
{
    public string Name => (Presses, Long) switch
    {
        (1, false) => "single",
        (2, false) => "double",
        (3, false) => "triple",
        (1, true) => "long",
        (var n, false) => $"{n}x",
        (var n, true) => $"{n - 1}x + long",
    };
}

/// <summary>
/// Groups key edges into gestures, by timing alone: a press that goes down within <see cref="Gap"/> of the previous
/// press's release joins its gesture, a press held for <see cref="LongPress"/> or more is long and ends its gesture, and
/// another key ends it too. Repeated downs while a key is held (auto-repeat) are part of that press. Pure: time comes
/// in with the edges and with <see cref="Flush"/>, so it is tested without a keyboard.
/// </summary>
/// <remarks>
/// A headset may classify gestures itself (a double press sent as Next), and Windows may give a press with no real
/// hold time; the spike logs the raw edges beside the gestures, so a wrong window shows in the log.
/// </remarks>
public sealed class GestureClassifier(TimeSpan gap, TimeSpan longPress)
{
    public static readonly TimeSpan DefaultGap = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan DefaultLongPress = TimeSpan.FromMilliseconds(800);

    private int? _key;
    private int _presses;
    private bool _held;
    private TimeSpan _start;
    private TimeSpan _pressedAt;
    private TimeSpan _releasedAt;

    public GestureClassifier() : this(DefaultGap, DefaultLongPress) { }

    public TimeSpan Gap { get; } = gap;
    public TimeSpan LongPress { get; } = longPress;

    /// <summary>Takes one edge; returns the gestures it ended (the one before it, or itself for a long press).</summary>
    public IReadOnlyList<Gesture> Feed(KeyEdge edge)
    {
        List<Gesture> ended = [];
        if (_key is { } key && (key != edge.Key || (edge.Down && !_held && edge.At - _releasedAt > Gap)))
            ended.AddRange(End());

        if (edge.Down)
        {
            if (_held && _key == edge.Key) return ended; // auto-repeat
            if (_key is null)
            {
                _key = edge.Key;
                _start = edge.At;
            }
            _presses++;
            _held = true;
            _pressedAt = edge.At;
        }
        else if (_held && _key == edge.Key)
        {
            _held = false;
            _releasedAt = edge.At;
            if (edge.At - _pressedAt >= LongPress)
                ended.AddRange(End(longPress: true));
        }
        // An up with no down (the hook started mid-press) is dropped
        return ended;
    }

    /// <summary>Ends the gesture once no press can join it any more: nothing held, and <see cref="Gap"/> passed since its release.</summary>
    public Gesture? Flush(TimeSpan now) =>
        _key is not null && !_held && now - _releasedAt > Gap ? End().Single() : null;

    private IEnumerable<Gesture> End(bool longPress = false)
    {
        var gesture = new Gesture(_key!.Value, _presses, longPress, _start, _held ? _pressedAt : _releasedAt);
        _key = null;
        _presses = 0;
        _held = false;
        return [gesture];
    }
}
