using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Models;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// Opening a project settles its item by kind (issue #592, <see cref="IProjectManager.MarkOpenedAsync"/>): what is on
/// screen where the user lands, the last turn or a pending request, is seen or acknowledged; what is above it stays.
/// </summary>
public class OpenedTests
{
    private const string Question = "Which branch should I push to?";
    private static readonly DateTime At = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = At.AddMinutes(10);

    private static ProjectStatus StatusOf(ProjectState state) =>
        new("p/r/1", "worker", state, At.AddDays(-1), At, null, new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0);

    private static ProjectStatus Finished => StatusOf(ProjectState.Idle) with { LastResult = "All done.", LastResultAt = At };

    private static Task WaitForResultAsync(LifecycleHarness harness, string projectId, string result) =>
        harness.WaitForStatusPushAsync(projectId, status => status.LastResult == result && status.State == ProjectState.Idle);

    // ── The rules ──

    [Fact]
    public void Finished_IsSeen()
    {
        var opened = Attention.Opened(Finished, Now);

        Assert.Equal(Now, opened.SeenAt);
        Assert.Null(Attention.Of(opened));
    }

    [Fact]
    public void AQuestionInPlainText_IsSeen_AndStillHeldForAReply()
    {
        var asking = StatusOf(ProjectState.Stopped) with { CurrentQuestion = Question, QuestionAt = At, Outcome = TurnOutcome.Blocked };
        Assert.False(asking.QuestionSeen);

        var opened = Attention.Opened(asking, Now);

        Assert.Null(Attention.Of(opened));
        Assert.Equal(Question, opened.CurrentQuestion);
        Assert.True(opened.QuestionSeen);
        Assert.True(ProjectSummary.Of(opened).QuestionSeen);
    }

    [Fact]
    public void ALatestEscalation_IsCleared_AndWhatIsUnderItSettledToo()
    {
        var escalated = Finished with { Escalation = new Escalation(At.AddMinutes(-1), "#376 needs your decision.") };
        Assert.Equal(AttentionKind.Escalation, Attention.Of(escalated)?.Kind);

        var opened = Attention.Opened(escalated, Now);

        Assert.Null(opened.Escalation);
        Assert.Null(Attention.Of(opened));
    }

    [Fact]
    public void AnEarlierEscalation_Stays_AndTheLastTurnUnderItIsSeen()
    {
        var escalated = Attention.TurnStarted(Finished with { Escalation = new Escalation(At.AddMinutes(-5), "#376 needs your decision.") });
        Assert.True(escalated.Escalation!.Earlier);

        var opened = Attention.Opened(escalated, Now);

        Assert.Equal(AttentionKind.Escalation, Attention.Of(opened)?.Kind);
        Assert.Null(Attention.Of(opened with { Escalation = null }));
    }

    [Theory]
    [InlineData(AttentionKind.Error)]
    [InlineData(AttentionKind.Review)]
    public void AnErrorOrAReview_WhichTheViewDoesNotShow_Stays(AttentionKind kind)
    {
        var status = kind == AttentionKind.Error
            ? StatusOf(ProjectState.Error) with { LastError = "fatal: not a git repository" }
            : StatusOf(ProjectState.Idle) with
            {
                PullRequest = new PullRequestStatus("https://example.test/pr/7", 7, PullRequestState.Open, PullRequestReview.ChangesRequested, At),
            };

        var opened = Attention.Opened(status, Now);

        Assert.Equal(status, opened);
        Assert.Equal(kind, Attention.Of(opened)?.Kind);
    }

    [Fact]
    public void APendingAskUserQuestion_IsAcknowledged_NotAnswered()
    {
        var question = new PendingQuestion("q1", [new QuestionItem("Which color?", null, [new QuestionOption("Red", null)], false)], At);
        var asking = StatusOf(ProjectState.WaitingInput) with
        {
            PendingQuestion = question, CurrentQuestion = "Which color?", QuestionAt = At, Importance = Importance.Important,
        };
        Assert.Equal((false, AttentionAlert.Interrupt), (Attention.Of(asking)!.Acknowledged, Attention.Of(asking)!.Alert));

        var opened = Attention.Opened(asking, Now);

        var item = Attention.Of(opened)!;
        Assert.Equal((AttentionKind.Question, true, AttentionAlert.Inbox), (item.Kind, item.Acknowledged, item.Alert));
        Assert.Same(question, opened.PendingQuestion);
        // A question asked after it is new
        var next = question with { RequestId = "q2", RequestedAt = Now.AddMinutes(1) };
        Assert.False(Attention.Of(opened with { PendingQuestion = next })!.Acknowledged);
    }

    [Fact]
    public void NothingToSettle_ChangesNothing()
    {
        var idle = StatusOf(ProjectState.Idle);
        Assert.Equal(idle, Attention.Opened(idle, Now));
    }

    // ── Through the server ──

    [Fact]
    public async Task OpeningAFinishedProject_ClearsIt_AndAnotherTurnRaisesItAgain()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit()
            .AwaitStdin().EmitResult("first")
            .AwaitStdin().EmitResult("second")
            .AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await WaitForResultAsync(harness, created.Id, "first");
        Assert.Equal(AttentionKind.Finished, Assert.Single(harness.Projects.GetAttention()).Kind);

        await harness.Projects.MarkOpenedAsync(created.Id);
        Assert.Empty(harness.Projects.GetAttention());
        await harness.RestartAsync(resume: false);
        Assert.Empty(harness.Projects.GetAttention());
    }

    [Fact]
    public async Task OpeningAProjectWaitingOnAPermission_AcknowledgesIt_AndOnlyTheAnswerClearsIt()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(created.Id);
        await LifecycleHarness.WaitUntilAsync(async () => (await harness.Projects.GetStatusAsync(created.Id)).OutputOffset > 0, null,
            () => $"the init line was not handled.\n{harness.Describe(created.Id)}");
        var request = harness.Projects.RequestPermissionAsync(created.Id,
            new PermissionPromptRequest("Bash", JsonSerializer.SerializeToElement(new { command = "git push" }), "toolu_1"), CancellationToken.None);
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.Projects.GetAttention().Length == 1), null,
            () => "the permission was not listed");
        var pushes = harness.Hub.AttentionPushes.Count;

        await harness.Projects.MarkOpenedAsync(created.Id);

        var item = Assert.Single(harness.Projects.GetAttention());
        Assert.Equal((AttentionKind.Permission, true, AttentionAlert.Inbox), (item.Kind, item.Acknowledged, item.Alert));
        // The acknowledgement is pushed, for the app and the notifiers to drop it
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.Hub.AttentionPushes.Count > pushes), null,
            () => "the acknowledgement was not pushed");
        Assert.True(harness.Hub.AttentionPushes[^1].Single().Acknowledged);
        Assert.NotNull((await harness.Projects.GetStatusAsync(created.Id)).PendingPermission);

        await harness.Projects.RespondToPermissionAsync(created.Id, item.Permission!.RequestId, new PermissionDecision(true));
        await request.WaitAsync(LifecycleHarness.DefaultTimeout);
        Assert.Empty(harness.Projects.GetAttention());
    }

    [Fact]
    public async Task AnEscalationATurnCameAfter_StaysWhenOpened_AndTheLatestIsCleared()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit()
            .AwaitStdin().EmitResult("first")
            .AwaitStdin().EmitUser("Worker 3 is done.", echo: true).EmitResult("second")
            .AwaitStdin());
        var overseer = await harness.CreateProjectAsync("overseer");
        await WaitForResultAsync(harness, overseer.Id, "first");
        await harness.Projects.EscalateAsync(overseer.Id, "#376 needs your decision.", null);

        await harness.Projects.SendOrHoldAsync(overseer.Id, "Worker 3 is done.", senderId: null);
        await WaitForResultAsync(harness, overseer.Id, "second");
        await harness.Projects.MarkOpenedAsync(overseer.Id);

        var item = Assert.Single(harness.Projects.GetAttention());
        Assert.Equal((AttentionKind.Escalation, "#376 needs your decision."), (item.Kind, item.Text));

        // Asked again, it is the latest thing: opening clears it
        await harness.Projects.EscalateAsync(overseer.Id, "#377 needs your decision.", null);
        await harness.Projects.MarkOpenedAsync(overseer.Id);
        Assert.Empty(harness.Projects.GetAttention());
    }
}
