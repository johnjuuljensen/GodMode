using System.Collections.Concurrent;
using GodMode.Maui;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodMode.Voice.Tests;

/// <summary>
/// Issue #442's media sessions (GodMode.Maui's <c>MediaSessionTracker</c>, compiled in from its source): what Windows'
/// sessions tell <see cref="HeadsetButtons"/> and the music's pause, when the manager comes late, when an app restarts,
/// and how GodMode's own session is told from the others. Over fake sessions.
/// </summary>
public sealed class MediaSessionTrackerTests : IDisposable
{
    private readonly List<FakeSession> _listed = [];
    private readonly ConcurrentQueue<string> _otherPlaying = new();
    private readonly MediaSessionTracker<FakeSession> _tracker;
    private FakeSession? _current;

    public MediaSessionTrackerTests()
    {
        _tracker = new MediaSessionTracker<FakeSession>(s => s.AppId, s => s.Playing, s => s.Watched = true, s => s.Watched = false,
            NullLogger.Instance);
        _tracker.OtherPlaying += _otherPlaying.Enqueue;
    }

    public void Dispose() => _tracker.Dispose();

    private void Arrive() => _tracker.Arrived(() => [.. _listed], () => _current);

    private FakeSession Add(string appId, bool playing)
    {
        var session = new FakeSession(appId) { Playing = playing };
        _listed.Add(session);
        return session;
    }

    private void Plays(FakeSession session, bool playing)
    {
        session.Playing = playing;
        _tracker.StatusChanged(session);
    }

    // ── A manager that comes late (item 1) ──

    [Fact]
    public void A_manager_that_arrives_after_GodModes_session_opened_with_the_music_paused_leaves_it_paused()
    {
        var own = new OwnSession();
        using var buttons = new HeadsetButtons(own, _tracker, () => Task.CompletedTask, NullLogger.Instance);
        // The music cannot be told yet: open playing, as a new session that plays is current
        Assert.True(own.Playing);

        Add("Spotify.exe", playing: false);
        Arrive();

        Assert.False(own.Playing);
    }

    [Fact]
    public void Nothing_is_told_before_the_sessions_arrive()
    {
        Assert.Null(_tracker.OthersPlaying);
        var spotify = Add("Spotify.exe", playing: true);
        _tracker.Listed();
        _tracker.StatusChanged(spotify);
        Assert.Empty(_otherPlaying);
        Assert.False(spotify.Watched);

        Arrive();
        Assert.True(_tracker.OthersPlaying);
        Assert.Equal(["Spotify.exe"], _otherPlaying);
    }

    // ── A restarted app (item 3) ──

    [Fact]
    public void An_app_that_restarts_is_watched_again_and_told_when_its_new_session_plays()
    {
        var first = Add("Spotify.exe", playing: false);
        Arrive();
        Assert.True(first.Watched);

        _listed.Clear();
        _tracker.Listed();
        Assert.False(first.Watched);

        var second = Add("Spotify.exe", playing: false);
        _tracker.Listed();
        Assert.True(second.Watched);
        Plays(second, true);

        Assert.Equal(["Spotify.exe"], _otherPlaying);
        Assert.True(_tracker.OthersPlaying);
    }

    [Fact]
    public void An_app_whose_session_is_replaced_in_one_change_is_told_when_the_new_one_plays()
    {
        var first = Add("Spotify.exe", playing: true);
        Arrive();
        Assert.Equal(["Spotify.exe"], _otherPlaying);

        _listed.Clear();
        var second = Add("Spotify.exe", playing: false);
        _tracker.Listed();
        Assert.False(first.Watched);
        Assert.False(_tracker.OthersPlaying);

        Plays(second, true);
        Assert.Equal(["Spotify.exe", "Spotify.exe"], _otherPlaying);
    }

    [Fact]
    public void A_session_handed_back_as_a_new_object_is_not_told_again()
    {
        Add("Spotify.exe", playing: true);
        Arrive();

        _listed[0] = new FakeSession("Spotify.exe") { Playing = true };
        _tracker.Listed();

        Assert.Equal(["Spotify.exe"], _otherPlaying);
    }

    // ── GodMode's own session (item 4) ──

    [Fact]
    public void GodModes_own_session_is_told_by_its_marker_whatever_its_app_ID()
    {
        var own = Add("Some.App.Id", playing: true);
        Arrive();
        Assert.True(_tracker.OthersPlaying);

        _tracker.Marked(own, OwnMediaSession.Marker);
        _current = own;

        Assert.True(_tracker.IsOwn(own));
        Assert.True(_tracker.OwnIsCurrent);
        Assert.False(_tracker.OthersPlaying);
        Plays(own, false);
        Plays(own, true);
        Assert.Equal(["Some.App.Id"], _otherPlaying);
    }

    /// <summary>A debug build beside the installed app: a pause sent to the other's session would toggle its mic.</summary>
    [Fact]
    public void Another_GodModes_session_is_neither_GodModes_own_nor_music()
    {
        var installed = Add("GodMode.Maui.exe", playing: false);
        Arrive();
        Assert.True(_tracker.IsOwn(installed));
        var changes = 0;
        _tracker.OthersChanged += () => changes++;

        _tracker.Marked(installed, OwnMediaSession.MarkerPrefix + (Environment.ProcessId + 1));
        Assert.Equal(1, changes);
        Assert.False(_tracker.IsOwn(installed));
        Assert.False(_tracker.IsMusic(installed));
        _current = installed;
        Assert.False(_tracker.OwnIsCurrent);

        Plays(installed, true);
        Assert.Empty(_otherPlaying);
        Assert.False(_tracker.OthersPlaying);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Music_is_neither_GodModes_own_nor_another_GodModes()
    {
        var spotify = Add("Spotify.exe", playing: false);
        var own = Add("GodMode.Maui.exe", playing: false);
        Arrive();

        Assert.True(_tracker.IsMusic(spotify));
        Assert.False(_tracker.IsMusic(own));
        _tracker.Marked(own, OwnMediaSession.Marker);
        Assert.False(_tracker.IsMusic(own));
    }

    [Fact]
    public void A_session_with_no_marker_is_GodModes_by_its_app_ID()
    {
        var own = Add("GodMode.Maui.exe", playing: true);
        var spotify = Add("Spotify.exe", playing: false);
        Arrive();
        _tracker.Marked(spotify, "Rock");

        Assert.True(_tracker.IsOwn(own));
        Assert.False(_tracker.IsOwn(spotify));
        Assert.False(_tracker.OthersPlaying);
        Assert.Empty(_otherPlaying);
    }

    private sealed class FakeSession(string appId)
    {
        public string AppId { get; } = appId;
        public bool? Playing { get; set; }
        public bool Watched { get; set; }
    }

    private sealed class OwnSession : IOwnMediaSession
    {
        public bool Playing { get; private set; }

        public event Action<MediaButton>? Pressed
        {
            add { }
            remove { }
        }

        public void Open(bool playing) => Playing = playing;

        public void SetPlaying(bool playing) => Playing = playing;

        public Task ReopenAsync()
        {
            Playing = true;
            return Task.CompletedTask;
        }

        public void Close() { }
    }
}
