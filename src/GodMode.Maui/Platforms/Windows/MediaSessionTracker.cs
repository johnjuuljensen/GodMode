using Microsoft.Extensions.Logging;

namespace GodMode.Maui;

/// <summary>
/// How GodMode tells its own media session from the others (issue #442): its session's display carries
/// <see cref="Marker"/>, with this process's id, so neither an app ID without "GodMode" in it nor another GodMode
/// beside it (a debug build beside the installed app) is mistaken for it. Another GodMode's session is no music either:
/// a pause or a play sent to it would be a press of its headset's button, its mic's switch.
/// </summary>
public static class OwnMediaSession
{
    public const string MarkerPrefix = "GodMode voice ";

    /// <summary>What GodMode's session shows as its genre, and reads back from Windows' sessions.</summary>
    public static string Marker { get; } = MarkerPrefix + Environment.ProcessId;
}

/// <summary>
/// Windows' media sessions as voice follows them (<see cref="WindowsMediaPlayback"/>'s GSMTC sessions, issues #422,
/// #423 and #442), apart from Windows so it is tested: each session is watched while the sessions list it, by the
/// session itself, so an app that restarts (a new session, the same app ID) is watched again; nothing is told before
/// the sessions have arrived, and their arrival is told, so a manager that comes late is mirrored. A session is
/// GodMode's own when it shows <see cref="OwnMediaSession.Marker"/>; while no marker is read from it, when its app ID
/// names GodMode. One with another GodMode's marker is neither GodMode's own nor music. Compiled into the voice tests from its source, as <see cref="HeadsetButtons"/> is.
/// </summary>
/// <param name="appId">A session's app ID.</param>
/// <param name="playing">Whether a session plays; null when that cannot be read.</param>
/// <param name="watch">Starts listening to a session's changes, under the tracker's lock.</param>
/// <param name="unwatch">Stops it, under the tracker's lock.</param>
public sealed class MediaSessionTracker<TSession>(Func<TSession, string> appId, Func<TSession, bool?> playing,
    Action<TSession> watch, Action<TSession> unwatch, ILogger logger) : IMediaSessions where TSession : class
{
    private enum Whose
    {
        Own,
        // Another GodMode's: never paused, played or mirrored
        OtherGodMode,
        Music,
    }

    private sealed class Entry(string appId)
    {
        public string AppId { get; } = appId;
        public bool? Playing { get; set; }
        public string? Marker { get; set; }
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<TSession, Entry> _watched = new(ReferenceEqualityComparer.Instance);
    private Func<IReadOnlyList<TSession>>? _list;
    private Func<TSession?>? _current;
    private bool _disposed;

    /// <summary>A session started playing: its app ID. On any thread.</summary>
    public event Action<string>? Playing;

    public event Action<string>? OtherPlaying;

    public event Action? OthersChanged;

    public bool? OthersPlaying
    {
        get
        {
            TSession[] others;
            lock (_lock)
            {
                if (_current is null) return null;
                others = [.. _watched.Where(w => WhoseIs(w.Value) == Whose.Music).Select(w => w.Key)];
            }
            try
            {
                return others.Any(s => playing(s) == true);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public bool OwnIsCurrent
    {
        get
        {
            Func<TSession?>? current;
            lock (_lock) current = _current;
            try
            {
                return current?.Invoke() is { } session && IsOwn(session);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Whether <paramref name="session"/> is GodMode's own.</summary>
    public bool IsOwn(TSession session) => WhoseIs(session) == Whose.Own;

    /// <summary>Whether <paramref name="session"/> is music voice pauses and resumes: neither GodMode's own nor another GodMode's.</summary>
    public bool IsMusic(TSession session) => WhoseIs(session) == Whose.Music;

    private Whose WhoseIs(TSession session)
    {
        lock (_lock)
        {
            if (_watched.TryGetValue(session, out var entry)) return WhoseIs(entry);
        }
        return WhoseIs(null, appId(session));
    }

    private static Whose WhoseIs(Entry entry) => WhoseIs(entry.Marker, entry.AppId);

    private static Whose WhoseIs(string? marker, string appId) => marker switch
    {
        _ when marker == OwnMediaSession.Marker => Whose.Own,
        // A debug build's beside the installed app
        not null when marker.StartsWith(OwnMediaSession.MarkerPrefix, StringComparison.Ordinal) => Whose.OtherGodMode,
        _ when appId.Contains("GodMode", StringComparison.OrdinalIgnoreCase) => Whose.Own,
        _ => Whose.Music,
    };

    /// <summary>
    /// The sessions are there (Windows: the manager arrived): <paramref name="list"/> lists them, and
    /// <paramref name="current"/> is the one the buttons go to. From here they are told, so the first status changes
    /// reach those who mirror them.
    /// </summary>
    public void Arrived(Func<IReadOnlyList<TSession>> list, Func<TSession?> current)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _list = list;
            _current = current;
        }
        Listed();
    }

    /// <summary>
    /// The sessions changed: the new ones are watched and told, the ones no longer listed are let go of. Before they
    /// have arrived, nothing: <see cref="Arrived"/> lists them.
    /// </summary>
    public void Listed()
    {
        var added = new List<(TSession Session, bool New)>();
        lock (_lock)
        {
            if (_disposed || _list is null) return;
            var sessions = _list();
            var listed = new HashSet<TSession>(sessions, ReferenceEqualityComparer.Instance);
            var gone = _watched.Where(w => !listed.Contains(w.Key)).ToList();
            foreach (var (session, _) in gone)
            {
                _watched.Remove(session);
                unwatch(session);
            }
            foreach (var session in sessions.Where(s => !_watched.ContainsKey(s)))
            {
                var entry = new Entry(appId(session));
                // One that left as another came, of the same app, in one change: what was known of it carries over, so
                // a session Windows hands back as a new object is not told again; a restarted app is told what changed
                var was = gone.FindIndex(g => g.Value.AppId == entry.AppId);
                if (was >= 0)
                {
                    entry.Playing = gone[was].Value.Playing;
                    entry.Marker = gone[was].Value.Marker;
                    gone.RemoveAt(was);
                }
                added.Add((session, was < 0));
                _watched[session] = entry;
                watch(session);
            }
        }
        foreach (var (session, isNew) in added)
        {
            if (isNew)
                logger.LogInformation("Voice: media session {AppId}{Own}", appId(session), IsOwn(session) ? ", GodMode's own by its app ID" : "");
            StatusChanged(session);
        }
        // One that went away may have been the music playing
        OthersChanged?.Invoke();
    }

    /// <summary>A session's playback changed: only a change of whether it plays is told.</summary>
    public void StatusChanged(TSession session)
    {
        bool? now;
        try
        {
            now = playing(session);
        }
        catch (Exception)
        {
            return;
        }
        if (now is not { } isPlaying) return;
        string id;
        bool music;
        lock (_lock)
        {
            if (_disposed || _current is null || !_watched.TryGetValue(session, out var entry) || entry.Playing == isPlaying) return;
            entry.Playing = isPlaying;
            id = entry.AppId;
            music = WhoseIs(entry) == Whose.Music;
        }
        if (isPlaying) Playing?.Invoke(id);
        if (!music) return;
        OthersChanged?.Invoke();
        if (isPlaying) OtherPlaying?.Invoke(id);
    }

    /// <summary>What a session's display carries as its genre, read back from Windows: whose it is.</summary>
    public void Marked(TSession session, string? marker)
    {
        lock (_lock)
        {
            if (_disposed || !_watched.TryGetValue(session, out var entry) || entry.Marker == marker) return;
            var was = WhoseIs(entry);
            entry.Marker = marker;
            var now = WhoseIs(entry);
            if (now == was && marker != OwnMediaSession.Marker) return;
            logger.LogInformation("Voice: media session {AppId} is {Whose}, by its marker", entry.AppId, now switch
            {
                Whose.Own => "GodMode's own",
                Whose.OtherGodMode => "another GodMode's: neither paused nor mirrored",
                _ => "music",
            });
        }
        OthersChanged?.Invoke();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var session in _watched.Keys) unwatch(session);
            _watched.Clear();
        }
    }
}
