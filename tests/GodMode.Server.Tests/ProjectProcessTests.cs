using System.Threading.Channels;
using GodMode.Server.Models;

namespace GodMode.Server.Tests;

/// <summary>
/// A project's output consumer that faults is replaced, and an in-order change never waits on a
/// consumer that cannot run it (#238): subscribe and Stop both wait on one.
/// </summary>
public class ProjectProcessTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static PipelineItem.InOrder Change(Action run) =>
        new(() => { run(); return Task.CompletedTask; }, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>Runs the in-order items it reads, as the real consumer does.</summary>
    private static async Task RunItemsAsync(ChannelReader<PipelineItem> items)
    {
        while (await items.WaitToReadAsync())
            while (items.TryRead(out var item))
                if (item is PipelineItem.InOrder { Done.Task.IsCompleted: false } inOrder)
                {
                    await inOrder.Change();
                    inOrder.Done.TrySetResult();
                }
    }

    [Fact]
    public async Task ConsumerThatFaults_IsReplaced_AndTheChangeRuns()
    {
        var process = new ProjectProcess();
        var starts = 0;
        var ran = false;

        var queued = await process.RunInOrderAsync(Change(() => ran = true), items =>
            Interlocked.Increment(ref starts) == 1 ? Task.FromException(new IOException("the disk is full")) : RunItemsAsync(items))
            .WaitAsync(Timeout);

        Assert.True(queued);
        Assert.True(ran);
        Assert.Equal(2, starts);
        await process.CloseAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task ConsumerThatFaultsAgain_FailsTheChange_InsteadOfWaiting_AndItNeverRunsLate()
    {
        var process = new ProjectProcess();
        var ran = false;
        var change = Change(() => ran = true);

        var waiting = process.RunInOrderAsync(change, _ => Task.FromException(new IOException("the disk is full")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => waiting.WaitAsync(Timeout));
        // A consumer that works again finds it given up on
        await process.RunInOrderAsync(Change(() => { }), RunItemsAsync).WaitAsync(Timeout);
        Assert.False(ran);
    }

    [Fact]
    public async Task ClosedPipeline_QueuesNothing()
    {
        var process = new ProjectProcess();
        await process.CloseAsync();

        Assert.False(await process.RunInOrderAsync(Change(() => { }), RunItemsAsync).WaitAsync(Timeout));
    }
}
