using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Shared.Enums;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// Issue #513: <c>/recap</c> reaches claude, and its answer is the session's recap (<c>Recap</c>, <c>RecapAt</c>), not a
/// turn's reply: the last result stays, and nothing needs the user for it. The hub's AskForRecap, which voice uses, sends
/// it once, only to an idle session with no recap. The lines are the ones claude 2.1.289 writes headless for <c>/recap</c>.
/// </summary>
public class RecapTests : IDisposable
{
    private const string RecapText = "Fixing the login redirect; the tests pass and the pull request is open.";

    private static readonly string SyntheticAssistant = JsonSerializer.Serialize(new
    {
        type = "assistant",
        message = new
        {
            model = "<synthetic>", role = "assistant", content = new[] { new { type = "text", text = RecapText } },
            usage = new { input_tokens = 0, output_tokens = 0 },
        },
        session_id = FakeScript.SessionIdPlaceholder,
    });

    private static readonly string RecapResult = JsonSerializer.Serialize(new
    {
        type = "result", subtype = "success", is_error = false, num_turns = 0, result = RecapText,
        session_id = FakeScript.SessionIdPlaceholder, usage = new { input_tokens = 0, output_tokens = 0 },
    });

    /// <summary>Held until the test lets the recap come (<see cref="LetRecapCome"/>).</summary>
    private readonly string _recapGate = Path.Combine(Path.GetTempPath(), $"godmode-recap-{Guid.NewGuid():N}");

    public void Dispose() => File.Delete(_recapGate);

    private void LetRecapCome() => File.WriteAllText(_recapGate, "");

    /// <summary>
    /// A first turn that answers "hello", then a <c>/recap</c>'s answer once the test lets it come (claude echoes the
    /// command as it takes it), then idle.
    /// </summary>
    private FakeScript Script() =>
        new FakeScript().EmitInit().AwaitStdin().EmitAssistant("hello").Sleep(50).EmitResult("hello")
            .AwaitStdin().EmitUser("/recap", echo: true).AwaitFile(_recapGate).Emit(SyntheticAssistant).Emit(RecapResult)
            .AwaitStdin();

    private static Task WaitForRecapAsync(LifecycleHarness harness, string projectId) =>
        LifecycleHarness.WaitUntilAsync(
            async () => (await harness.Projects.GetStatusAsync(projectId)) is { Recap: not null, State: ProjectState.Idle },
            null, () => $"the recap did not come.\n{harness.Describe(projectId)}");

    [Fact]
    public async Task AskForRecap_SendsRecapOnce_ItsAnswerIsTheRecap_AndTheLastResultStays()
    {
        await using var harness = new LifecycleHarness(Script());
        var created = await harness.CreateProjectAsync();
        var idle = await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        await harness.Projects.MarkSeenAsync(created.Id);
        var seenAt = (await harness.Projects.GetStatusAsync(created.Id)).SeenAt;

        Assert.Equal(RecapAsk.Sent, await harness.Projects.AskForRecapAsync(created.Id));
        // Asked again while it comes: nothing more is sent
        Assert.Equal(RecapAsk.Asked, await harness.Projects.AskForRecapAsync(created.Id));
        LetRecapCome();
        await WaitForRecapAsync(harness, created.Id);

        var status = await harness.Projects.GetStatusAsync(created.Id);
        Assert.Equal(RecapText, status.Recap);
        Assert.NotNull(status.RecapAt);
        Assert.Equal(("hello", idle.LastResultAt), (status.LastResult, status.LastResultAt));
        Assert.Equal(seenAt, status.SeenAt);
        Assert.DoesNotContain(harness.Projects.GetAttention(), item => item.ProjectId == created.Id);
        Assert.Equal(RecapText, harness.ReadStatusFile(created.Id).Recap);

        // It has its recap now: nothing more is sent
        Assert.Equal(RecapAsk.HasRecap, await harness.Projects.AskForRecapAsync(created.Id));
        var stdin = harness.Launches(created.Id)[0].Stdin;
        Assert.Equal(2, stdin.Count);
        Assert.Contains("\"text\":\"/recap\"", stdin[1]); // a command, never marked as speech
    }

    [Fact]
    public async Task ASessionWithARecap_IsSentNothing()
    {
        await using var harness = new LifecycleHarness(Script());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        // The user typed /recap in the app: the session has its recap, and voice never asked
        await harness.Projects.SendInputAsync(created.Id, "/recap");
        LetRecapCome();
        await WaitForRecapAsync(harness, created.Id);

        Assert.Equal(RecapAsk.HasRecap, await harness.Projects.AskForRecapAsync(created.Id));

        Assert.Equal(2, harness.Launches(created.Id)[0].Stdin.Count);
        Assert.Equal("hello", (await harness.Projects.GetStatusAsync(created.Id)).LastResult);
    }

    [Fact]
    public async Task ARunningSession_IsSentNothing_AndMayBeAskedOnceIdle()
    {
        var turnEnds = Path.Combine(Path.GetTempPath(), $"godmode-turn-{Guid.NewGuid():N}");
        try
        {
            var script = new FakeScript().EmitInit().AwaitStdin().EmitAssistant("working").AwaitFile(turnEnds).EmitResult("hello")
                .AwaitStdin().AwaitFile(_recapGate).Emit(SyntheticAssistant).Emit(RecapResult).AwaitStdin();
            await using var harness = new LifecycleHarness(script);
            var created = await harness.CreateProjectAsync();
            await harness.WaitForStdinAsync(created.Id);
            await harness.WaitForStateAsync(created.Id, ProjectState.Running);

            Assert.Equal(RecapAsk.Busy, await harness.Projects.AskForRecapAsync(created.Id));
            Assert.Single(harness.Launches(created.Id)[0].Stdin);

            // A busy session's ask sent nothing, so it does not count as its one
            File.WriteAllText(turnEnds, "");
            await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
            Assert.Equal(RecapAsk.Sent, await harness.Projects.AskForRecapAsync(created.Id));
            await harness.WaitForStdinAsync(created.Id, count: 2);
        }
        finally
        {
            File.Delete(turnEnds);
        }
    }

    [Fact]
    public async Task ASessionThatAsksTheUser_IsSentNothing()
    {
        var script = new FakeScript().EmitInit().AwaitStdin().EmitAssistant("Shall I merge it?").Sleep(50).EmitResult("Shall I merge it?")
            .AwaitStdin();
        await using var harness = new LifecycleHarness(script);
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.WaitingInput);

        Assert.Equal(RecapAsk.Busy, await harness.Projects.AskForRecapAsync(created.Id));

        Assert.Single(harness.Launches(created.Id)[0].Stdin);
        Assert.Equal(ProjectState.WaitingInput, (await harness.Projects.GetStatusAsync(created.Id)).State);
    }

    [Fact]
    public void Recap_IsPassed_AndIsNoTextToMark()
    {
        Assert.Null(SlashCommands.WhyRefused("/recap", null));
        Assert.Equal("/recap", SpokenInput.Mark("/recap"));
    }
}
