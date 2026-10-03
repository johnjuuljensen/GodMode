using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// What of the fleet reaches the user's list (issue #401): a child's Finished and Review are its parent's business, an
/// action with <c>quietTurns</c> raises Finished only for the turns the user started, and an overseer's escalation is
/// an item of its own that stays until the user has seen it.
/// </summary>
public class FleetInboxNoiseTests
{
    private const string Question = "Which branch should I push to?";

    /// <summary>A turn that ends with <paramref name="result"/>, then waits.</summary>
    private static FakeScript Finishing(string result) =>
        new FakeScript().EmitInit().AwaitStdin().EmitAssistant("Working on it.").Sleep(50).EmitResult(result).AwaitStdin();

    private static string Describe(IEnumerable<AttentionItem> items) =>
        "[" + string.Join(", ", items.Select(i => $"{i.ProjectId}:{i.Kind}:{i.Text}")) + "]";

    private static Task WaitForResultAsync(LifecycleHarness harness, string projectId, string result) =>
        harness.WaitForStatusPushAsync(projectId, status => status.LastResult == result && status.State == ProjectState.Idle);

    // ── Children ──

    [Fact]
    public async Task AChildsFinished_IsNotTheUsers_ButTheFleetSeesIt_AndTheParentIsRecordedOnIt()
    {
        await using var harness = new LifecycleHarness(Finishing("parent done"));
        var parent = await harness.CreateProjectAsync("parent");
        await WaitForResultAsync(harness, parent.Id, "parent done");
        harness.UseScript(Finishing("child done"));
        var child = await harness.CreateProjectAsync("child", parentId: parent.Id);
        await WaitForResultAsync(harness, child.Id, "child done");

        var mine = harness.Projects.GetAttention();
        var parentItem = Assert.Single(mine);
        Assert.Equal((parent.Id, AttentionKind.Finished, (string?)null), (parentItem.ProjectId, parentItem.Kind, parentItem.RecordedParentId));
        var all = harness.Projects.GetAllAttention();
        Assert.Equal([parent.Id, child.Id], all.Select(item => item.ProjectId));
        Assert.Equal((AttentionKind.Finished, parent.Id), (all[1].Kind, all[1].RecordedParentId));
        // The pushed list is the user's too
        Assert.Equal(Describe(mine), Describe(harness.Hub.AttentionPushes[^1]));
        // The app's list says who runs the child, for voice to leave it to its overseer (#469)
        var summaries = await harness.Projects.ListProjectsAsync();
        Assert.Equal(parent.Id, summaries.Single(s => s.Id == child.Id).RecordedParentId);
        Assert.Null(summaries.Single(s => s.Id == parent.Id).RecordedParentId);
    }

    [Fact]
    public async Task AChildsQuestion_IsTheUsers()
    {
        await using var harness = new LifecycleHarness(Finishing("parent done"));
        var parent = await harness.CreateProjectAsync("parent");
        await WaitForResultAsync(harness, parent.Id, "parent done");
        await harness.Projects.MarkSeenAsync(parent.Id);
        harness.UseScript(new FakeScript().EmitInit().AwaitStdin().EmitAssistant(Question).Sleep(50).EmitResult(Question).AwaitStdin());
        var child = await harness.CreateProjectAsync("child", parentId: parent.Id);
        await harness.WaitForStateAsync(child.Id, ProjectState.WaitingInput);

        var item = Assert.Single(harness.Projects.GetAttention());
        Assert.Equal((child.Id, AttentionKind.Question, parent.Id), (item.ProjectId, item.Kind, item.RecordedParentId));
    }

    /// <summary>
    /// The parent is the server's record, not the child's status.json: a session that names a parent there is still the
    /// user's.
    /// </summary>
    [Fact]
    public async Task AParentInTheSessionsOwnStatus_RecordsNoParent()
    {
        await using var harness = new LifecycleHarness(Finishing("done"));
        var session = await harness.CreateProjectAsync("session");
        await WaitForResultAsync(harness, session.Id, "done");
        var other = await harness.CreateProjectAsync("other");
        harness.Tracked(session.Id).Status = harness.Tracked(session.Id).Status with { ParentId = other.Id };

        Assert.Contains(harness.Projects.GetAttention(), item => item.ProjectId == session.Id && item.RecordedParentId == null);
    }

    [Theory]
    [InlineData(AttentionKind.Finished, null, true)]
    [InlineData(AttentionKind.Finished, "p/r/parent", false)]
    [InlineData(AttentionKind.Review, "p/r/parent", false)]
    [InlineData(AttentionKind.Permission, "p/r/parent", true)]
    [InlineData(AttentionKind.Question, "p/r/parent", true)]
    [InlineData(AttentionKind.Error, "p/r/parent", true)]
    [InlineData(AttentionKind.Escalation, "p/r/parent", true)]
    public void IsTheUsers_LeavesAChildsFinishedAndReviewToItsParent(AttentionKind kind, string? parent, bool theUsers) =>
        Assert.Equal(theUsers, Attention.IsTheUsers(new AttentionItem("p/r/s", "s", "p", "r", kind, DateTime.UtcNow, "text", RecordedParentId: parent)));

    // ── Quiet turns ──

    /// <summary>
    /// The quiet action's turns: the create's and a woken one raise no Finished, the user's does, and a woken one after it
    /// leaves the user's result listed until it is seen. The fleet's send is no turn of the user's. Each result still shows.
    /// </summary>
    [Fact]
    public async Task AQuietActionsTurns_RaiseFinished_OnlyWhenTheUserStartedThem()
    {
        var wake = Path.Combine(Path.GetTempPath(), $"godmode-wake-{Guid.NewGuid():N}");
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit()
                .AwaitStdin().EmitResult("first")
                .AwaitStdin().EmitResult("second")
                .AwaitFile(wake).EmitAssistant("A worker reported.").EmitResult("woken")
                .AwaitStdin().EmitResult("third")
                .AwaitStdin(),
            rootConfig: new Dictionary<string, object> { ["quietTurns"] = true });
        try
        {
            var overseer = await harness.CreateProjectAsync("overseer");
            await WaitForResultAsync(harness, overseer.Id, "first");
            Assert.Empty(harness.Projects.GetAttention());
            Assert.True(harness.ReadStatusFile(overseer.Id).QuietResult);

            // The user's input: its turn's end is the user's
            await harness.Projects.SendInputAsync(overseer.Id, "What decisions are needed?");
            await WaitForResultAsync(harness, overseer.Id, "second");
            var second = Assert.Single(harness.Projects.GetAttention());
            Assert.Equal("second", second.Text);

            // Woken on its own: the result shows, and the user's stays listed, as it was
            File.WriteAllText(wake, "");
            await WaitForResultAsync(harness, overseer.Id, "woken");
            var kept = Assert.Single(harness.Projects.GetAttention());
            Assert.Equal((AttentionKind.Finished, "second", second.Since), (kept.Kind, kept.Text, kept.Since));

            // Seen, and woken by the fleet's send: nothing for the user
            await harness.Projects.MarkSeenAsync(overseer.Id);
            await harness.Projects.SendOrHoldAsync(overseer.Id, "Worker 3 is done.", senderId: null);
            await WaitForResultAsync(harness, overseer.Id, "third");
            Assert.Empty(harness.Projects.GetAttention());
        }
        finally
        {
            File.Delete(wake);
        }
    }

    [Fact]
    public async Task AQuietTurn_StaysQuiet_AfterARestart()
    {
        await using var harness = new LifecycleHarness(Finishing("first"), rootConfig: new Dictionary<string, object> { ["quietTurns"] = true });
        var overseer = await harness.CreateProjectAsync("overseer");
        await WaitForResultAsync(harness, overseer.Id, "first");

        await harness.RestartAsync();

        Assert.Empty(harness.Projects.GetAttention());
        Assert.Equal("first", (await harness.Projects.GetStatusAsync(overseer.Id)).LastResult);
    }

    [Fact]
    public async Task AnActionWithoutQuietTurns_RaisesFinished_ForEveryTurn()
    {
        await using var harness = new LifecycleHarness(Finishing("first"));
        var session = await harness.CreateProjectAsync("session");
        await WaitForResultAsync(harness, session.Id, "first");

        Assert.Equal("first", Assert.Single(harness.Projects.GetAttention()).Text);
    }

    // ── Escalation ──

    [Fact]
    public async Task AnEscalation_StaysListed_ThroughTurnsAndARestart_UntilItIsSeen()
    {
        var wake = Path.Combine(Path.GetTempPath(), $"godmode-wake-{Guid.NewGuid():N}");
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit()
                .AwaitStdin().EmitResult("first")
                .AwaitFile(wake).EmitResult("woken")
                .AwaitStdin(),
            rootConfig: new Dictionary<string, object> { ["quietTurns"] = true });
        try
        {
            var overseer = await harness.CreateProjectAsync("overseer");
            await WaitForResultAsync(harness, overseer.Id, "first");

            await harness.Projects.EscalateAsync(overseer.Id, "#376 and #384 need your decision.", "https://github.com/o/r/issues/376");
            var item = Assert.Single(harness.Projects.GetAttention());
            Assert.Equal((AttentionKind.Escalation, "#376 and #384 need your decision.", "https://github.com/o/r/issues/376"),
                (item.Kind, item.Text, item.PullRequestUrl));

            File.WriteAllText(wake, "");
            await WaitForResultAsync(harness, overseer.Id, "woken");
            await harness.RestartAsync(resume: false);
            Assert.Equal(item.Since, Assert.Single(harness.Projects.GetAttention()).Since);

            await harness.Projects.MarkSeenAsync(overseer.Id);
            Assert.Empty(harness.Projects.GetAttention());
        }
        finally
        {
            File.Delete(wake);
        }
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("Decide.", "file:///etc/passwd")]
    [InlineData("Decide.", "not a url")]
    public async Task AnEscalation_WithNoText_OrAUrlThatIsNotHttp_IsRefused(string text, string? url)
    {
        await using var harness = new LifecycleHarness(Finishing("first"));
        var session = await harness.CreateProjectAsync("session");
        await WaitForResultAsync(harness, session.Id, "first");

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Projects.EscalateAsync(session.Id, text, url));
        Assert.Null((await harness.Projects.GetStatusAsync(session.Id)).Escalation);
    }
}
