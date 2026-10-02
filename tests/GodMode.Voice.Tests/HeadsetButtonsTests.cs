using System.Collections.Concurrent;
using GodMode.Maui;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodMode.Voice.Tests;

/// <summary>
/// Issue #423's headset buttons (GodMode.Maui's <c>HeadsetButtons</c>, compiled in from its source): GodMode's media
/// session mirrors the music's play/pause, takes the current session back when other music starts playing (checked at
/// 300 ms and 1 s), and passes Play and Pause on as the mic's switch, ignoring Next and Previous. Over fakes of Windows'
/// sessions, on a clock the test moves.
/// </summary>
public sealed class HeadsetButtonsTests : IDisposable
{
    private readonly ManualTime _time = new();
    private readonly FakeSessions _sessions = new();
    private readonly FakeOwnSession _own;
    private int _presses;
    private HeadsetButtons? _buttons;

    public HeadsetButtonsTests() => _own = new FakeOwnSession(_sessions);

    private HeadsetButtons Start() => _buttons = new HeadsetButtons(_own, _sessions, () =>
    {
        Interlocked.Increment(ref _presses);
        return Task.CompletedTask;
    }, NullLogger.Instance, _time);

    public void Dispose() => _buttons?.Dispose();

    // ── Mirroring ──

    [Fact]
    public void It_opens_paused_when_nothing_plays_and_playing_when_music_does()
    {
        _sessions.Others = false;
        Start();
        Assert.Equal(["open paused"], _own.Log);

        _buttons!.Dispose();
        _own.Log.Clear();
        _sessions.Others = true;
        Start();
        Assert.Equal(["open playing"], _own.Log);
    }

    [Fact]
    public void Its_status_follows_the_musics_play_and_pause()
    {
        _sessions.Others = true;
        Start();

        _sessions.Change(playing: false);
        Assert.False(_own.Playing);
        _sessions.Change(playing: true);
        Assert.True(_own.Playing);
        _sessions.Change(playing: false);
        Assert.False(_own.Playing);
    }

    [Fact]
    public void While_the_music_cannot_be_told_the_status_stays_as_it_is()
    {
        _sessions.Others = false;
        Start();
        _sessions.Others = null;
        _sessions.Changed();
        Assert.False(_own.Playing);
    }

    // ── Reclaiming ──

    [Fact]
    public async Task Music_that_starts_playing_and_takes_the_current_session_is_taken_back_after_300_ms()
    {
        _sessions.Others = false;
        Start();
        _sessions.StartsPlaying("Spotify", takesCurrent: true);
        Assert.True(_own.Playing);

        _time.Advance(TimeSpan.FromMilliseconds(290));
        Assert.Equal(0, _own.Reopens);
        _time.Advance(TimeSpan.FromMilliseconds(20));
        await Eventually.UntilAsync(() => _own.Reopens == 1, () => $"{_own.Reopens} reopens");
        Assert.True(_sessions.OwnIsCurrent);

        // Current again at the 1 s check: nothing more
        _time.Advance(TimeSpan.FromSeconds(1.1));
        await Task.Delay(50);
        Assert.Equal(1, _own.Reopens);
    }

    [Fact]
    public async Task A_reopen_that_does_not_take_it_back_is_tried_again_at_1_s()
    {
        _sessions.Others = false;
        Start();
        _own.ReopenTakesCurrent = false;
        _sessions.StartsPlaying("Spotify", takesCurrent: true);

        _time.Advance(TimeSpan.FromMilliseconds(300));
        await Eventually.UntilAsync(() => _own.Reopens == 1, () => $"{_own.Reopens} reopens");
        _own.ReopenTakesCurrent = true;
        _time.Advance(TimeSpan.FromMilliseconds(990));
        await Task.Delay(50);
        Assert.Equal(1, _own.Reopens);
        _time.Advance(TimeSpan.FromMilliseconds(20));
        await Eventually.UntilAsync(() => _own.Reopens == 2, () => $"{_own.Reopens} reopens");
        Assert.True(_sessions.OwnIsCurrent);
        // Reopened playing, with the music playing: the status still mirrors it
        Assert.True(_own.Playing);
    }

    [Fact]
    public async Task Music_that_starts_playing_while_GodMode_stays_current_is_left_alone()
    {
        _sessions.Others = false;
        Start();
        _sessions.StartsPlaying("Spotify", takesCurrent: false);

        _time.Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.Equal(0, _own.Reopens);
        Assert.True(_own.Playing);
    }

    [Fact]
    public async Task Once_voice_is_off_nothing_is_taken_back()
    {
        _sessions.Others = false;
        Start();
        _sessions.StartsPlaying("Spotify", takesCurrent: true);
        _buttons!.Dispose();

        _time.Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.Equal(0, _own.Reopens);
        Assert.Equal("close", _own.Log.Last());
    }

    // ── The buttons ──

    [Fact]
    public async Task Play_and_Pause_are_the_mics_switch_and_Next_and_Previous_are_ignored()
    {
        Start();
        _own.Press(MediaButton.Pause);
        _own.Press(MediaButton.Next);
        _own.Press(MediaButton.Previous);
        _own.Press(MediaButton.Play);

        await Eventually.UntilAsync(() => _presses == 2, () => $"{_presses} presses");
        await Task.Delay(50);
        Assert.Equal(2, _presses);
    }

    [Fact]
    public async Task Stopping_closes_the_session_and_its_buttons_do_nothing_after()
    {
        Start();
        _buttons!.Dispose();
        Assert.Equal("close", _own.Log.Last());

        _own.Press(MediaButton.Pause);
        _sessions.Change(playing: true);
        await Task.Delay(50);
        Assert.Equal(0, _presses);
        Assert.Equal("close", _own.Log.Last());
    }

    private sealed class FakeSessions : IMediaSessions
    {
        public bool? Others { get; set; }
        public bool OwnIsCurrent { get; set; } = true;

        public event Action<string>? OtherPlaying;
        public event Action? OthersChanged;

        public bool? OthersPlaying => Others;

        public void Changed() => OthersChanged?.Invoke();

        public void Change(bool playing)
        {
            Others = playing;
            OthersChanged?.Invoke();
        }

        /// <summary>As Windows does: the session that starts playing may become the current one.</summary>
        public void StartsPlaying(string id, bool takesCurrent)
        {
            Others = true;
            if (takesCurrent) OwnIsCurrent = false;
            OthersChanged?.Invoke();
            OtherPlaying?.Invoke(id);
        }
    }

    private sealed class FakeOwnSession(FakeSessions sessions) : IOwnMediaSession
    {
        public ConcurrentQueue<string> Log { get; } = new();
        public bool Playing { get; private set; }
        public bool ReopenTakesCurrent { get; set; } = true;
        private int _reopens;
        public int Reopens => Volatile.Read(ref _reopens);

        public event Action<MediaButton>? Pressed;

        public void Press(MediaButton button) => Pressed?.Invoke(button);

        public void Open(bool playing)
        {
            Playing = playing;
            Log.Enqueue($"open {(playing ? "playing" : "paused")}");
        }

        public void SetPlaying(bool playing)
        {
            if (Playing == playing) return;
            Playing = playing;
            Log.Enqueue(playing ? "playing" : "paused");
        }

        public Task ReopenAsync()
        {
            Playing = true;
            if (ReopenTakesCurrent) sessions.OwnIsCurrent = true;
            Log.Enqueue("reopen");
            Interlocked.Increment(ref _reopens);
            return Task.CompletedTask;
        }

        public void Close() => Log.Enqueue("close");
    }
}
