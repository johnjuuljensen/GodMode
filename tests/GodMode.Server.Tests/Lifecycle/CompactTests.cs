using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// Issue #579: the <c>compact</c> tool on GodMode's MCP endpoint schedules a compaction the session asked for: when its
/// turn ends, the server sends <c>/compact</c> with its instructions, and when that is done, its continuation, labelled as
/// its own. Nothing between raises a Finished. What arrives meanwhile is held until the continuation's turn has ended, but
/// the user's input, which cancels the continuation; a stop, an exit or a turn that waits on the user drops it all, and a
/// failed compaction still sends the continuation. The lines are the ones claude 2.1.287 writes headless for <c>/compact</c>.
/// </summary>
public class CompactTests : IDisposable
{
    private const string Instructions = "keep the pull request number and the plan";
    private const string Continuation = "Carry on with issue 579: check the review threads on pull request 612.";

    private const string CompactBoundary =
        """{"type":"system","subtype":"compact_boundary","session_id":"{{session_id}}","compact_metadata":{"trigger":"manual","pre_tokens":28063,"post_tokens":1916}}""";

    /// <summary>The result claude writes after /compact: no text, and the model took no turn.</summary>
    private const string SilentResult =
        """{"type":"result","subtype":"success","is_error":false,"num_turns":0,"result":"","session_id":"{{session_id}}","usage":{"input_tokens":0,"output_tokens":0}}""";

    /// <summary>Gates the fake waits at until the test lets it go (<see cref="Let"/>).</summary>
    private readonly string _turnEnds = Gate("turn-ends");
    private readonly string _compacted = Gate("compacted");

    private static string Gate(string name) => Path.Combine(Path.GetTempPath(), $"godmode-compact-{name}-{Guid.NewGuid():N}");

    private static void Let(string gate) => File.WriteAllText(gate, "");

    public void Dispose()
    {
        File.Delete(_turnEnds);
        File.Delete(_compacted);
    }

    private static FakeScript CallCompact(FakeScript script, string continuation = Continuation, string? instructions = Instructions) =>
        script.CallTool(ProjectManager.McpServerName, CompactTool.Name,
            instructions == null ? new { continuation } : new { continuation, instructions });

    /// <summary>
    /// The prompt's turn calls <c>compact</c>, waits at <see cref="_turnEnds"/>, and ends; the <c>/compact</c> it then reads
    /// is echoed, waits at <see cref="_compacted"/>, and ends with <paramref name="compactResult"/> (done, by default);
    /// the next input's turn answers "carried on". Then a last turn, which answers "and the rest".
    /// </summary>
    private FakeScript Script(string? compactResult = null)
    {
        var script = CallCompact(new FakeScript().EmitInit().AwaitStdin().EmitUser("Open the pull request", echo: true))
            .AwaitFile(_turnEnds).EmitAssistant("The pull request is open.").Sleep(50).EmitResult("opened")
            .AwaitStdin().EmitUser("/compact " + Instructions, echo: true).AwaitFile(_compacted);
        script = compactResult == null ? script.Emit(CompactBoundary).Emit(SilentResult) : script.Emit(compactResult);
        return script.AwaitStdin().EmitUser("continuation", echo: true).EmitAssistant("Carrying on.").Sleep(50).EmitResult("carried on")
            .AwaitStdin().EmitUser("rest", echo: true).EmitAssistant("And the rest.").Sleep(50).EmitResult("and the rest")
            .AwaitStdin();
    }

    private static Task WaitForResultAsync(LifecycleHarness harness, string projectId, string result) =>
        LifecycleHarness.WaitUntilAsync(
            async () => (await harness.Projects.GetStatusAsync(projectId)) is { State: ProjectState.Idle } status && status.LastResult == result,
            null, () => $"the turn that answers '{result}' did not end.\n{harness.Describe(projectId)}");

    private static bool Finished(AttentionItem item, string projectId) => item.ProjectId == projectId && item.Kind == AttentionKind.Finished;

    [Fact]
    public async Task Compact_EndsTheTurn_SendsCompactWithItsInstructions_ThenTheContinuation_AndNoFinishedBetween()
    {
        await using var harness = new LifecycleHarness(Script(), mcpEndpoint: true);
        var created = await harness.CreateProjectAsync();
        var called = await harness.WaitForLaunchAsync(created.Id, l => l.Calls.Count > 0);
        Assert.Equal($"{CompactTool.Name} {CompactTool.Scheduled}", Assert.Single(called.Calls));
        var pushesBefore = harness.Hub.StatusPushes(created.Id).Count;
        var attentionBefore = harness.Hub.AttentionPushes.Count;

        // The turn ends: /compact goes with the instructions, and the session is still at work
        Let(_turnEnds);
        var compacting = await harness.WaitForStdinAsync(created.Id, count: 2);
        Assert.Contains($"\"text\":\"/compact {Instructions}\"", compacting.Stdin[1]);
        var meanwhile = await harness.Projects.GetStatusAsync(created.Id);
        Assert.Equal(ProjectState.Running, meanwhile.State);
        Assert.Equal("opened", meanwhile.LastResult);

        // The compaction is done: the continuation goes, labelled as the session's own
        Let(_compacted);
        var continued = await harness.WaitForStdinAsync(created.Id, count: 3);
        Assert.Contains(System.Text.Json.JsonSerializer.Serialize(CompactTool.ContinuationOf(Continuation, null))[1..^1], continued.Stdin[2]);
        // Nothing between the call and the continuation said the session was done
        var pushes = harness.Hub.StatusPushes(created.Id).Skip(pushesBefore).ToList();
        var attention = harness.Hub.AttentionPushes.Skip(attentionBefore).ToList();
        Assert.All(pushes, status => Assert.Equal(ProjectState.Running, status.State));
        Assert.DoesNotContain(attention.SelectMany(items => items), item => Finished(item, created.Id));
        await WaitForResultAsync(harness, created.Id, "carried on");

        // The continuation's turn is one as any: it ends in a Finished
        Assert.Contains(harness.Projects.GetAttention(), item => Finished(item, created.Id) && item.Text.Contains("carried on"));
        // And claude is done with the /compact: the tool may be called again
        Assert.False(harness.Tracked(created.Id).Process.CompactWritten);
        Assert.Null(harness.Tracked(created.Id).Process.Compaction);
    }

    [Fact]
    public async Task WithNoInstructions_CompactIsSentAlone()
    {
        var script = CallCompact(new FakeScript().EmitInit().AwaitStdin(), instructions: null)
            .EmitResult("opened").AwaitStdin().Emit(CompactBoundary).Emit(SilentResult)
            .AwaitStdin().EmitResult("carried on").AwaitStdin();
        await using var harness = new LifecycleHarness(script, mcpEndpoint: true);
        var created = await harness.CreateProjectAsync();

        await WaitForResultAsync(harness, created.Id, "carried on");

        var stdin = harness.Launches(created.Id)[0].Stdin;
        Assert.Equal(3, stdin.Count);
        Assert.Contains("\"text\":\"/compact\"", stdin[1]);
        Assert.Contains("Continuation, after a compaction", stdin[2]);
    }

    [Fact]
    public async Task AFleetSendMeanwhile_IsHeld_AndFollowsTheContinuationsTurn()
    {
        await using var harness = new LifecycleHarness(Script(), mcpEndpoint: true);
        var created = await harness.CreateProjectAsync();
        await harness.WaitForLaunchAsync(created.Id, l => l.Calls.Count > 0);

        // Before the turn ends, and while it compacts: held, both
        var first = await harness.Projects.SendOrHoldAsync(created.Id, "first from the overseer", senderId: null);
        Assert.False(first.Delivered);
        Assert.Contains("compacting", first.Held);
        Let(_turnEnds);
        await harness.WaitForStdinAsync(created.Id, count: 2);
        var second = await harness.Projects.SendOrHoldAsync(created.Id, "second from the overseer", senderId: null);
        Assert.False(second.Delivered);
        Let(_compacted);

        // The continuation goes first; what was held follows once its turn has ended, in order, as one input
        var launch = await harness.WaitForStdinAsync(created.Id, count: 4);
        Assert.Contains("Continuation, after a compaction", launch.Stdin[2]);
        Assert.Contains("first from the overseer", launch.Stdin[3]);
        Assert.True(launch.Stdin[3].IndexOf("first", StringComparison.Ordinal) < launch.Stdin[3].IndexOf("second", StringComparison.Ordinal));
        await WaitForResultAsync(harness, created.Id, "and the rest");
    }

    [Fact]
    public async Task TheUsersInput_BeforeTheTurnEnds_CancelsTheCompactionAndTheContinuation()
    {
        var script = CallCompact(new FakeScript().EmitInit().AwaitStdin())
            .AwaitFile(_turnEnds).EmitAssistant("Opened.").Sleep(50).EmitResult("opened")
            .AwaitStdin().EmitUser("Do this instead", echo: true).EmitAssistant("Done instead.").Sleep(50).EmitResult("done instead")
            .AwaitStdin();
        await using var harness = new LifecycleHarness(script, mcpEndpoint: true);
        var created = await harness.CreateProjectAsync();
        await harness.WaitForLaunchAsync(created.Id, l => l.Calls.Count > 0);

        await harness.Projects.SendInputAsync(created.Id, "Do this instead");
        Let(_turnEnds);
        await WaitForResultAsync(harness, created.Id, "done instead");

        var stdin = harness.Launches(created.Id)[0].Stdin;
        Assert.Equal(2, stdin.Count);
        Assert.DoesNotContain(stdin, line => line.Contains("/compact") || line.Contains("Continuation, after"));
    }

    [Fact]
    public async Task TheUsersInput_WhileItCompacts_CancelsTheContinuation_AndFollowsTheCompaction()
    {
        var script = CallCompact(new FakeScript().EmitInit().AwaitStdin())
            .EmitAssistant("Opened.").Sleep(50).EmitResult("opened")
            .AwaitStdin().AwaitFile(_compacted).Emit(CompactBoundary).Emit(SilentResult)
            .AwaitStdin().EmitUser("Do this instead", echo: true).EmitAssistant("Done instead.").Sleep(50).EmitResult("done instead")
            .AwaitStdin();
        await using var harness = new LifecycleHarness(script, mcpEndpoint: true);
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(created.Id, count: 2);

        await harness.Projects.SendInputAsync(created.Id, "Do this instead");
        Let(_compacted);
        await WaitForResultAsync(harness, created.Id, "done instead");

        var stdin = harness.Launches(created.Id)[0].Stdin;
        Assert.Equal(3, stdin.Count);
        Assert.Contains("/compact", stdin[1]);
        Assert.Contains("Do this instead", stdin[2]);
        Assert.DoesNotContain(stdin, line => line.Contains("Continuation, after"));
    }

    [Fact]
    public async Task AFailedCompaction_StillSendsTheContinuation_SayingSo()
    {
        var failed = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "result", subtype = "error_during_execution", is_error = true, num_turns = 0, result = "Not enough messages to compact.",
            session_id = FakeScript.SessionIdPlaceholder, usage = new { input_tokens = 0, output_tokens = 0 },
        });
        await using var harness = new LifecycleHarness(Script(failed), mcpEndpoint: true);
        var created = await harness.CreateProjectAsync();
        await harness.WaitForLaunchAsync(created.Id, l => l.Calls.Count > 0);
        Let(_turnEnds);
        Let(_compacted);

        await WaitForResultAsync(harness, created.Id, "carried on");

        var stdin = harness.Launches(created.Id)[0].Stdin;
        Assert.Contains("The compaction failed (Not enough messages to compact.)", stdin[2]);
        Assert.Contains("Carry on with issue 579", stdin[2]);
        var status = await harness.Projects.GetStatusAsync(created.Id);
        Assert.Null(status.LastError);
        Assert.DoesNotContain(harness.Hub.StatusPushes(created.Id), pushed => pushed.State == ProjectState.Error);
    }

    [Fact]
    public async Task AStopBetween_DropsIt_AndTheResumeDoesNotReplayIt()
    {
        await using var harness = new LifecycleHarness(Script(), mcpEndpoint: true);
        var created = await harness.CreateProjectAsync();
        await harness.WaitForLaunchAsync(created.Id, l => l.Calls.Count > 0);

        await harness.Projects.StopProjectAsync(created.Id);
        await harness.WaitForStateAsync(created.Id, ProjectState.Stopped);
        Assert.Null(harness.Tracked(created.Id).Process.Compaction);

        harness.UseScript(new FakeScript().EmitInit().AwaitStdin().EmitAssistant("Resumed.").Sleep(50).EmitResult("resumed").AwaitStdin());
        await harness.Projects.ReplyAndResumeAsync(created.Id, "go on");
        await WaitForResultAsync(harness, created.Id, "resumed");

        Assert.Single(harness.Launches(created.Id)[0].Stdin);
        Assert.Single(harness.Launches(created.Id)[1].Stdin);
        Assert.DoesNotContain(harness.Launches(created.Id)[1].Stdin, line => line.Contains("/compact") || line.Contains("Continuation, after"));
    }

    [Fact]
    public async Task ATurnThatEndsOnAQuestion_DropsIt()
    {
        var script = CallCompact(new FakeScript().EmitInit().AwaitStdin())
            .EmitAssistant("Shall I merge it?").Sleep(50).EmitResult("Shall I merge it?").AwaitStdin();
        await using var harness = new LifecycleHarness(script, mcpEndpoint: true);
        var created = await harness.CreateProjectAsync();

        await harness.WaitForStateAsync(created.Id, ProjectState.WaitingInput);

        Assert.Single(harness.Launches(created.Id)[0].Stdin);
        Assert.Null(harness.Tracked(created.Id).Process.Compaction);
    }

    [Fact]
    public async Task ASecondCall_AnEmptyOrlongContinuation_AreRefused()
    {
        var longContinuation = Continuation + " " + new string('x', SessionInbox.AdvertisedMaxTextLength);
        // The one scheduled is over the advertised length, and under the enforced one (issue #590)
        var script = CallCompact(CallCompact(CallCompact(CallCompact(new FakeScript().EmitInit().AwaitStdin(), continuation: " "),
                continuation: new string('x', SessionInbox.MaxTextLength + 1)), continuation: longContinuation), continuation: "a second one")
            .EmitResult("opened").AwaitStdin().Emit(CompactBoundary).Emit(SilentResult)
            .AwaitStdin().EmitResult("carried on").AwaitStdin();
        await using var harness = new LifecycleHarness(script, mcpEndpoint: true);
        var created = await harness.CreateProjectAsync();

        await WaitForResultAsync(harness, created.Id, "carried on");

        var launch = harness.Launches(created.Id)[0];
        Assert.Equal(4, launch.Calls.Count);
        Assert.StartsWith($"{CompactTool.Name} error:", launch.Calls[0]);
        Assert.Contains("empty", launch.Calls[0]);
        Assert.StartsWith($"{CompactTool.Name} error:", launch.Calls[1]);
        Assert.Contains($"at most {SessionInbox.AdvertisedMaxTextLength} are sent", launch.Calls[1]);
        Assert.Equal($"{CompactTool.Name} {CompactTool.Scheduled}", launch.Calls[2]);
        Assert.StartsWith($"{CompactTool.Name} error:", launch.Calls[3]);
        Assert.Contains("already pending", launch.Calls[3]);
        // The first that was scheduled is the one that runs
        Assert.Contains(Continuation, launch.Stdin[2]);
        Assert.DoesNotContain("a second one", launch.Stdin[2]);
    }

    [Fact]
    public async Task ACallWhileTheSessionIsInACompactItDidNotAskFor_IsRefused()
    {
        var script = CallCompact(new FakeScript().EmitInit().AwaitStdin().AwaitFile(_turnEnds))
            .EmitResult("opened").AwaitStdin().Emit(CompactBoundary).Emit(SilentResult).AwaitStdin();
        await using var harness = new LifecycleHarness(script, mcpEndpoint: true);
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(created.Id);

        // The user's /compact, queued behind the turn
        await harness.Projects.SendInputAsync(created.Id, "/compact");
        Let(_turnEnds);
        var launch = await harness.WaitForLaunchAsync(created.Id, l => l.Calls.Count > 0);
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(!harness.Tracked(created.Id).Process.CompactWritten), null,
            () => $"claude was not done with the user's /compact.\n{harness.Describe(created.Id)}");
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);

        Assert.StartsWith($"{CompactTool.Name} error:", launch.Calls[0]);
        Assert.Contains("did not ask for", launch.Calls[0]);
        Assert.Equal(2, harness.Launches(created.Id)[0].Stdin.Count);
    }
}
