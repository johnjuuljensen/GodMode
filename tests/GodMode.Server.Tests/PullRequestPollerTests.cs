using GodMode.Server.Services;
using GodMode.Server.Tests.Lifecycle;
using GodMode.Shared.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodMode.Server.Tests;

/// <summary>The schedule of pull request checks: one at a time per project, coalesced, polled only while open.</summary>
public class PullRequestPollerTests
{
    /// <summary>A check that counts its calls, records overlap, and waits for <see cref="Release"/> when gated.</summary>
    private sealed class Checks
    {
        private int _running;
        public int Calls;
        public int MaxRunning;
        public PullRequestPoller.Outcome Outcome = PullRequestPoller.Outcome.Wait;
        /// <summary>When set, the checks up to this one say Poll, and the rest Wait.</summary>
        public int? PollUntil;
        public TaskCompletionSource? Gate;
        public readonly TaskCompletionSource FirstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PullRequestPoller.Outcome> CheckAsync(string projectId, CancellationToken cancel)
        {
            var call = Interlocked.Increment(ref Calls);
            var running = Interlocked.Increment(ref _running);
            lock (this) MaxRunning = Math.Max(MaxRunning, running);
            FirstStarted.TrySetResult();
            try
            {
                if (Gate is { } gate) await gate.Task.WaitAsync(cancel);
                return PollUntil is { } last ? call < last ? PullRequestPoller.Outcome.Poll : PullRequestPoller.Outcome.Wait : Outcome;
            }
            finally { Interlocked.Decrement(ref _running); }
        }
    }

    private static PullRequestPoller Poller(Checks checks, TimeSpan? interval = null) =>
        new(checks.CheckAsync, interval ?? TimeSpan.FromHours(1), NullLogger.Instance);

    [Fact]
    public async Task AsksDuringACheck_MakeOneMoreCheck_NeverTwoAtOnce()
    {
        var checks = new Checks { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var poller = Poller(checks);

        poller.CheckNow("p");
        await checks.FirstStarted.Task.WaitAsync(LifecycleHarness.DefaultTimeout);
        for (var i = 0; i < 10; i++) poller.CheckNow("p");
        checks.Gate.SetResult();

        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(checks.Calls == 2), null, () => $"{checks.Calls} checks, not 2");
        await Task.Delay(200);
        Assert.Equal(2, checks.Calls);
        Assert.Equal(1, checks.MaxRunning);
    }

    [Fact]
    public async Task Transitions_ToIdleOrStopped_Check()
    {
        var checks = new Checks();
        await using var poller = Poller(checks);

        poller.Remember("p", ProjectState.Stopped);
        poller.Observe("p", ProjectState.Stopped);
        poller.Observe("p", ProjectState.Running);
        poller.Observe("p", ProjectState.WaitingInput);
        await Task.Delay(200);
        Assert.Equal(0, checks.Calls);

        poller.Observe("p", ProjectState.Idle);
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(checks.Calls == 1), null, () => $"{checks.Calls} checks after Idle");
        poller.Observe("p", ProjectState.Idle);
        poller.Observe("p", ProjectState.Stopped);
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(checks.Calls == 2), null, () => $"{checks.Calls} checks after Stopped");
        await Task.Delay(200);
        Assert.Equal(2, checks.Calls);
    }

    [Fact]
    public async Task Open_IsPolled_UntilACheckSaysOtherwise()
    {
        var checks = new Checks { PollUntil = 4 };
        await using var poller = Poller(checks, TimeSpan.FromMilliseconds(50));

        poller.CheckNow("p");
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(checks.Calls >= 4), null, () => $"{checks.Calls} checks while polling");
        await Task.Delay(300);

        Assert.Equal(4, checks.Calls);
    }

    [Fact]
    public async Task Forget_CancelsTheRunningCheck_AndStopsPolling()
    {
        var checks = new Checks { Outcome = PullRequestPoller.Outcome.Poll, Gate = new TaskCompletionSource() };
        await using var poller = Poller(checks, TimeSpan.FromMilliseconds(50));

        poller.CheckNow("p");
        await checks.FirstStarted.Task.WaitAsync(LifecycleHarness.DefaultTimeout);
        await poller.ForgetAsync("p").WaitAsync(LifecycleHarness.DefaultTimeout);
        await Task.Delay(200);

        Assert.Equal(1, checks.Calls);
    }

    [Fact]
    public async Task Gone_EndsTheProjectsLoop_AndStop_StartsNoMore()
    {
        var checks = new Checks { Outcome = PullRequestPoller.Outcome.Gone };
        await using var poller = Poller(checks, TimeSpan.FromMilliseconds(50));

        poller.CheckNow("p");
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(checks.Calls == 1), null, () => $"{checks.Calls} checks");
        poller.Stop();
        poller.CheckNow("p");
        poller.Observe("q", ProjectState.Idle);
        await Task.Delay(200);

        Assert.Equal(1, checks.Calls);
    }
}
