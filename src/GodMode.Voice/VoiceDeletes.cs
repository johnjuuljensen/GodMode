namespace GodMode.Voice;

/// <summary>
/// A session voice would delete (#532), as its read-back says it: its name, whether it is running, whether its pull
/// request is still open, and whether it is only forgotten: an adopted session was a folder before GodMode had it, and
/// keeps it, as the app's "Forget (keep the folder)" does.
/// </summary>
public sealed record DeleteTarget(ProjectRef Project, SpokenName Name, bool Running, int? OpenPullRequest, bool Forget);

/// <summary>The sessions one read-back asks to delete, said together.</summary>
public sealed record DeleteRequest(IReadOnlyList<DeleteTarget> Targets)
{
    /// <summary>What is deleted, as the log says it.</summary>
    public string What => string.Join(", ", Targets.Select(t => t.Project.ProjectId));
}

/// <summary>A delete read-back playing, or played: the sessions it is for, its text, and when it started playing.</summary>
public sealed record ArmedDelete(DeleteRequest Request, string ReadBack, DateTimeOffset At);

/// <summary>What a delete did to one session: deleted (or forgotten), or why not.</summary>
public sealed record DeleteOutcome(DeleteTarget Target, string? Error);

/// <summary>
/// Voice's deletes (#532), as its creates (<see cref="SessionCreates"/>): the model's <see cref="VoiceTools.DeleteSession"/>
/// only proposes, the code reads back what goes in its own words (<see cref="VoicePhrases.DeleteReadBack"/>,
/// <see cref="ReadBackNode"/>), and only the user's yes to that read-back deletes (<see cref="ConfirmDeleteNode"/>). The
/// yes is armed when the read-back starts playing (<see cref="Spoken"/>), and <see cref="ConfirmWindow"/> passing, or
/// other speech of the bot's, drops it. Each session goes as on screen: <c>DeleteProject</c>, never forced, so the root's
/// delete script may refuse; an adopted one is forgotten, its folder kept. While a delete waits on the user
/// (<see cref="Waiting"/>), announcements are held (<see cref="HeldAnnouncements"/>).
/// </summary>
public sealed class SessionDeletes(IGodModeServers servers, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private DeleteRequest? _proposed;
    private (DeleteRequest Request, string ReadBack, DateTimeOffset At)? _toSay;
    private ArmedDelete? _armed;
    private bool _dropped;
    private bool _wasWaiting;
    private ITimer? _timer;
    private Action<IReadOnlyList<DeleteOutcome>>? _announce;
    private Task _running = Task.CompletedTask;

    /// <summary>How long after its read-back started playing a delete waits on the yes.</summary>
    public TimeSpan ConfirmWindow { get; init; } = TimeSpan.FromSeconds(20);

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>Nothing waits on the user any more (<see cref="Waiting"/>): what was held may be said.</summary>
    public event Action? Released;

    /// <summary>The deletes confirmed, until each has finished and been announced.</summary>
    public Task Running => Volatile.Read(ref _running);

    /// <summary>From now on, each confirmed delete's outcomes go to <paramref name="announce"/>.</summary>
    public void Attach(Action<IReadOnlyList<DeleteOutcome>> announce) => _announce = announce;

    /// <summary>The delete whose read-back is playing or played, waiting on the user's yes; null once dropped or expired.</summary>
    public ArmedDelete? Armed
    {
        get
        {
            ArmedDelete? armed;
            lock (_lock)
            {
                Expire();
                armed = _armed;
            }
            Notify();
            return armed;
        }
    }

    /// <summary>Whether a delete waits on the user: proposed and about to be read back, or read back and unanswered.</summary>
    public bool Waiting
    {
        get
        {
            lock (_lock) return WaitingNow();
        }
    }

    private bool WaitingNow()
    {
        Expire();
        return _proposed is not null || _toSay is not null || _armed is not null;
    }

    private void Expire()
    {
        var now = Now;
        if (_armed is { } armed && now - armed.At > ConfirmWindow || _toSay is { } toSay && now - toSay.At > ConfirmWindow)
            Drop();
    }

    private void Drop()
    {
        _toSay = null;
        _armed = null;
        _dropped = true;
    }

    /// <summary>Says, once, that nothing waits any more (<see cref="Released"/>), and while something does, looks again when it would expire.</summary>
    private void Notify()
    {
        bool released;
        lock (_lock)
        {
            var waiting = WaitingNow();
            released = _wasWaiting && !waiting;
            _wasWaiting = waiting;
            _timer?.Dispose();
            _timer = null;
            if ((_armed?.At ?? _toSay?.At) is { } at)
            {
                var due = at + ConfirmWindow - Now + TimeSpan.FromMilliseconds(50);
                _timer = _time.CreateTimer(_ => Notify(), null, due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
        }
        if (released) Released?.Invoke();
    }

    /// <summary>The chat's evaluation is done: whatever it settled, what no longer waits is released.</summary>
    public void Settled() => Notify();

    /// <summary><paramref name="request"/> is to be read back in place of the model's reply (<see cref="ReadBackNode"/>); any delete that waited is dropped.</summary>
    public void Propose(DeleteRequest request)
    {
        lock (_lock)
        {
            _proposed = request;
            _toSay = null;
            _armed = null;
            _dropped = false;
        }
        Notify();
    }

    /// <summary>The delete proposed in this evaluation, left for <see cref="TakeProposed"/>; null when none is.</summary>
    public DeleteRequest? Proposed
    {
        get
        {
            lock (_lock) return _proposed;
        }
    }

    /// <summary>The delete proposed in this evaluation, for its read-back to be said.</summary>
    public DeleteRequest? TakeProposed()
    {
        lock (_lock)
        {
            var proposed = _proposed;
            _proposed = null;
            return proposed;
        }
    }

    /// <summary><paramref name="readBack"/> is what the bot says next, for <paramref name="request"/>: it arms it when it starts playing.</summary>
    public void ReadingBack(DeleteRequest request, string readBack)
    {
        lock (_lock) _toSay = (request, readBack.Trim(), Now);
        Notify();
    }

    /// <summary>
    /// The bot started saying <paramref name="text"/>: the read-back arms its delete; anything else said while one waits
    /// drops it, and a yes to it is told nothing waits.
    /// </summary>
    public void Spoken(string text)
    {
        lock (_lock)
        {
            Expire();
            if (_toSay is { } toSay && toSay.ReadBack == text.Trim())
            {
                _armed = new ArmedDelete(toSay.Request, toSay.ReadBack, Now);
                _toSay = null;
                _dropped = false;
            }
            else if (_toSay is not null || _armed is not null)
                Drop();
        }
        Notify();
    }

    /// <summary>Whether a delete was read back and dropped unanswered since; asking forgets it.</summary>
    public bool TakeDropped()
    {
        lock (_lock)
        {
            var dropped = _dropped;
            _dropped = false;
            return dropped;
        }
    }

    /// <summary>The delete read back is dropped: the user said anything but yes. The one dropped, or null when none waited.</summary>
    public DeleteRequest? Cancel()
    {
        DeleteRequest? cancelled;
        lock (_lock)
        {
            cancelled = _armed?.Request;
            _armed = null;
            _toSay = null;
        }
        Notify();
        return cancelled;
    }

    /// <summary>
    /// The user said yes to <paramref name="armed"/>: its sessions are deleted one after the other, and the outcomes
    /// announced together when they are done. Null when it no longer waits (dropped, or another proposed since).
    /// </summary>
    public DeleteRequest? Confirm(ArmedDelete armed)
    {
        lock (_lock)
        {
            if (_armed != armed)
                return null;
            _armed = null;
        }
        Notify();
        var previous = Running;
        Volatile.Write(ref _running, Task.Run(async () =>
        {
            await previous;
            List<DeleteOutcome> outcomes = [];
            foreach (var target in armed.Request.Targets)
                outcomes.Add(await DeleteAsync(target));
            _announce?.Invoke(outcomes);
        }));
        return armed.Request;
    }

    private async Task<DeleteOutcome> DeleteAsync(DeleteTarget target)
    {
        try
        {
            if (target.Forget)
                await servers.ForgetAsync(target.Project, CancellationToken.None);
            else
                await servers.DeleteAsync(target.Project, CancellationToken.None);
            return new DeleteOutcome(target, null);
        }
        catch (Exception ex)
        {
            // As a create's: what is said of it is short (VoicePhrases.Deleted), and the server's log has it all
            return new DeleteOutcome(target, SessionCreates.ServerError(ex));
        }
    }
}
