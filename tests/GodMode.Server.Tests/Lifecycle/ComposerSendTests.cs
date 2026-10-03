using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Models;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// The user writes in the project's own composer, not in the inbox item's reply box (issue #440): both are the hub's
/// ReplyAndResume, and it drops the project's item whatever its kind and state, and the item does not come back.
/// What the user has not written does not see it: input held for the session and sent later. Covered elsewhere:
/// a running project's Finished (<see cref="AttentionTests.Reply_SeesTheResult_AndTheNextResultIsFinishedAgain"/>,
/// which is also the quick next turn: a new Finished, for the turn the reply started), a running plain-text
/// Question (<see cref="AttentionTests.QuestionInPlainText_IsSeen_AndStillAnswered"/>) and an escalation
/// (<see cref="FleetInboxNoiseTests.TheUsersReply_ClearsAnEscalation"/>).
/// </summary>
public class ComposerSendTests
{
    private const string Reply = "Carry on";

    /// <summary>A launch that has read its first input and works on, without ending its turn.</summary>
    private static FakeScript Working() =>
        new FakeScript().AwaitStdin().EmitInit().EmitAssistant("Working on it.").AwaitStdin();

    private static Task WaitForResultAsync(LifecycleHarness harness, string projectId, string result) =>
        harness.WaitForStatusPushAsync(projectId, status => status.LastResult == result && status.State == ProjectState.Idle);

    private static AttentionItem Listed(LifecycleHarness harness, AttentionKind kind)
    {
        var item = Assert.Single(harness.Projects.GetAttention());
        Assert.Equal(kind, item.Kind);
        return item;
    }

    /// <summary>The list after the send is empty: the last one pushed, and the one computed now.</summary>
    private static void Cleared(LifecycleHarness harness)
    {
        Assert.Empty(harness.Projects.GetAttention());
        Assert.Empty(harness.Hub.AttentionPushes[^1]);
    }

    [Fact]
    public async Task AReview_IsCleared_AndDoesNotComeBackWhenTheTurnEnds()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit()
            .AwaitStdin().EmitResult("first")
            .AwaitStdin().Sleep(100).EmitResult("second")
            .AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await WaitForResultAsync(harness, created.Id, "first");
        await harness.Projects.MarkSeenAsync(created.Id);
        // What the status script reports: changes requested on its pull request, after the user saw the result
        var info = harness.Tracked(created.Id);
        await harness.Lifecycle.UpdateStatusAsync(info, status => status with
        {
            PullRequest = new PullRequestStatus("https://example.test/pr/7", 7, PullRequestState.Open, PullRequestReview.ChangesRequested, DateTime.UtcNow),
        });
        await harness.Lifecycle.NotifyStatusChangedAsync(info);
        Listed(harness, AttentionKind.Review);

        await harness.Projects.ReplyAndResumeAsync(created.Id, Reply);

        Cleared(harness);
        await WaitForResultAsync(harness, created.Id, "second");
        Assert.Equal("second", Listed(harness, AttentionKind.Finished).Text);
    }

    [Fact]
    public async Task AStoppedSessionsFinished_IsCleared_ByTheReplyThatResumesIt()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin().EmitResult("first").AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await WaitForResultAsync(harness, created.Id, "first");
        await harness.Projects.StopProjectAsync(created.Id);
        await harness.WaitForStateAsync(created.Id, ProjectState.Stopped);
        Listed(harness, AttentionKind.Finished);
        harness.UseScript(Working());

        await harness.Projects.ReplyAndResumeAsync(created.Id, Reply);

        Assert.Equal(2, harness.Launches(created.Id).Count);
        Cleared(harness);
    }

    /// <summary>A plain-text question the session was stopped on (#426): it still asks, and the reply answers it.</summary>
    [Fact]
    public async Task AStoppedSessionsQuestion_IsCleared_ByTheReplyThatResumesIt()
    {
        const string question = "Which branch should I push to?";
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit()
            .AwaitStdin().EmitAssistant(question).Sleep(50).EmitResult(question).AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.WaitingInput);
        await harness.Projects.StopProjectAsync(created.Id);
        await harness.WaitForStateAsync(created.Id, ProjectState.Stopped);
        Assert.Equal(question, Listed(harness, AttentionKind.Question).Text);
        harness.UseScript(Working());

        await harness.Projects.ReplyAndResumeAsync(created.Id, "main");

        var resumed = await harness.WaitForStdinAsync(created.Id, index: 1);
        Assert.Contains("main", resumed.Stdin[0]);
        Cleared(harness);
    }

    [Fact]
    public async Task AnError_IsCleared_ByTheReplyThatResumesIt()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin().Stderr("fatal: not a git repository").Exit(1));
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Error);
        Listed(harness, AttentionKind.Error);
        harness.UseScript(Working());

        await harness.Projects.ReplyAndResumeAsync(created.Id, "Try again in the repo");

        Cleared(harness);
    }

    /// <summary>A permission prompt, and an AskUserQuestion of several questions, which a reply cannot answer one by one: both are denied with it.</summary>
    [Theory]
    [InlineData("Bash")]
    [InlineData("AskUserQuestion")]
    public async Task APendingRequest_IsAnsweredByTheReply_AndLeavesTheList(string tool)
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(created.Id);
        await LifecycleHarness.WaitUntilAsync(async () => (await harness.Projects.GetStatusAsync(created.Id)).OutputOffset > 0, null,
            () => harness.Describe(created.Id));
        var input = tool == "Bash"
            ? JsonSerializer.SerializeToElement(new { command = "rm -rf build" })
            : JsonSerializer.SerializeToElement(new
            {
                questions = new[] { "Which color?", "Which size?" }.Select(q => new { question = q, header = "", options = new[] { new { label = "A" }, new { label = "B" } }, multiSelect = false }),
            });
        var asking = harness.Projects.RequestPermissionAsync(created.Id, new PermissionPromptRequest(tool, input, "toolu_1"), CancellationToken.None);
        await harness.WaitForStatusPushAsync(created.Id, s => s.PendingPermission != null || s.PendingQuestion != null);
        Listed(harness, tool == "Bash" ? AttentionKind.Permission : AttentionKind.Question);

        await harness.Projects.ReplyAndResumeAsync(created.Id, "Neither: keep the build folder");

        Assert.Equal("deny", (await asking.WaitAsync(LifecycleHarness.DefaultTimeout)).Behavior);
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.Projects.GetAttention().Length == 0), null,
            () => $"still listed: {string.Join(", ", harness.Projects.GetAttention().Select(i => i.Kind))}");
        Assert.Empty(harness.Hub.AttentionPushes[^1]);
    }

    /// <summary>
    /// Input held for the session and sent when it can take it (a worker's message, a notice) is not the user's reply:
    /// on a quiet action the result the user has not seen stays listed through the turn it starts. The user's own
    /// send sees it, and what is listed next is the result of the turn the user started.
    /// </summary>
    [Fact]
    public async Task HeldInput_LeavesTheUnseenResult_TheUsersSendSeesIt()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit()
                .AwaitStdin().EmitResult("created")
                .AwaitStdin().EmitResult("first")
                .AwaitStdin().EmitResult("woken")
                .AwaitStdin().Sleep(100).EmitResult("replied")
                .AwaitStdin(),
            rootConfig: new Dictionary<string, object> { ["quietTurns"] = true });
        var created = await harness.CreateProjectAsync();
        await WaitForResultAsync(harness, created.Id, "created");
        // A turn the user started, whose result is the user's to see
        await harness.Projects.ReplyAndResumeAsync(created.Id, "Start on the epic");
        await WaitForResultAsync(harness, created.Id, "first");
        var first = Listed(harness, AttentionKind.Finished);
        Assert.Equal("first", first.Text);

        Assert.True(await harness.Lifecycle.TrySendHeldAsync(harness.Tracked(created.Id), "Worker 3 is done."));
        await WaitForResultAsync(harness, created.Id, "woken");
        var stays = Listed(harness, AttentionKind.Finished);
        Assert.Equal((first.Text, first.Since), (stays.Text, stays.Since));

        await harness.Projects.ReplyAndResumeAsync(created.Id, Reply);
        Cleared(harness);
        await WaitForResultAsync(harness, created.Id, "replied");
        Assert.Equal("replied", Listed(harness, AttentionKind.Finished).Text);
    }
}
