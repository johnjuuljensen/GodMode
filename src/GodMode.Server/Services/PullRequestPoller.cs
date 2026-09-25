using System.Collections.Concurrent;
using GodMode.Shared.Enums;

namespace GodMode.Server.Services;

/// <summary>
/// When each project's pull request is checked: at once on a transition to Idle or Stopped, and every
/// interval after a check that found it open. Each project has one loop, so it never has two checks at
/// once; asking while a check runs makes one more check after it. At most <see cref="MaxConcurrentChecks"/>
/// run across the server. Only the schedule is here, in memory; what a check finds is in status.json.
/// </summary>
public sealed class PullRequestPoller : IAsyncDisposable, IDisposable
{
    /// <summary>What a check says about the project's next one.</summary>
    public enum Outcome
    {
        /// <summary>Check it again on its next transition to Idle or Stopped.</summary>
        Wait,

        /// <summary>Its pull request is open: check it again after the interval, or sooner on a transition.</summary>
        Poll,

        /// <summary>The project is gone (deleted): forget it.</summary>
        Gone,
    }

    public const int MaxConcurrentChecks = 4;

    private readonly Func<string, CancellationToken, Task<Outcome>> _check;
    private readonly TimeSpan _interval;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly ConcurrentDictionary<string, ProjectState> _lastState = new();
    private readonly SemaphoreSlim _concurrency = new(MaxConcurrentChecks);
    private readonly CancellationTokenSource _stopping = new();

    private sealed class Entry(CancellationToken stopping)
    {
        public readonly CancellationTokenSource Cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        public readonly SemaphoreSlim Wake = new(0, 1);
        public volatile bool Polling;
        public Task Loop = Task.CompletedTask;
        public bool Started;
    }

    /// <param name="check">Checks the project, logging its own failures; throws only when cancelled.</param>
    public PullRequestPoller(Func<string, CancellationToken, Task<Outcome>> check, TimeSpan interval, ILogger logger)
    {
        _check = check;
        _interval = interval;
        _logger = logger;
    }

    /// <summary>
    /// A status push: checks the project when it went to Idle or Stopped from another state (or one
    /// not seen since <see cref="Remember"/>).
    /// </summary>
    public void Observe(string projectId, ProjectState state)
    {
        var previous = _lastState.TryGetValue(projectId, out var last) ? last : (ProjectState?)null;
        _lastState[projectId] = state;
        if (state is ProjectState.Idle or ProjectState.Stopped && previous != state)
            CheckNow(projectId);
    }

    /// <summary>The state a project is in without a push (recovered), so its next push is judged from it.</summary>
    public void Remember(string projectId, ProjectState state) => _lastState[projectId] = state;

    /// <summary>Checks the project as soon as a check may run, unless one is already due.</summary>
    public void CheckNow(string projectId)
    {
        if (_stopping.IsCancellationRequested) return;
        var entry = _entries.GetOrAdd(projectId, _ => new Entry(_stopping.Token));
        lock (entry)
        {
            if (!entry.Started)
            {
                entry.Started = true;
                entry.Loop = LoopAsync(projectId, entry);
            }
        }
        // Already due: one check covers both asks
        if (entry.Wake.CurrentCount == 0)
            try { entry.Wake.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>Stops checking the project, and waits for a check that is running to end (its script is killed).</summary>
    public async Task ForgetAsync(string projectId)
    {
        _lastState.TryRemove(projectId, out _);
        if (!_entries.TryRemove(projectId, out var entry)) return;
        await entry.Cancel.CancelAsync();
        await entry.Loop;
    }

    /// <summary>The server is stopping: no check starts from now on, and those running are cancelled.</summary>
    public void Stop() => _stopping.Cancel();

    private async Task LoopAsync(string projectId, Entry entry)
    {
        var cancel = entry.Cancel.Token;
        try
        {
            while (true)
            {
                if (entry.Polling) await entry.Wake.WaitAsync(_interval, cancel);
                else await entry.Wake.WaitAsync(cancel);

                Outcome outcome;
                await _concurrency.WaitAsync(cancel);
                try { outcome = await _check(projectId, cancel); }
                finally { _concurrency.Release(); }

                if (outcome == Outcome.Gone)
                {
                    _entries.TryRemove(KeyValuePair.Create(projectId, entry));
                    return;
                }
                entry.Polling = outcome == Outcome.Poll;
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // The check logs its own failures: this is a bug, and the project is no longer checked
            _logger.LogError(ex, "Pull request checks of project {ProjectId} stopped", projectId);
            _entries.TryRemove(KeyValuePair.Create(projectId, entry));
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        foreach (var entry in _entries.Values) await entry.Loop;
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
