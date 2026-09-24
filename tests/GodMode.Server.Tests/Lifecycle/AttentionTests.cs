using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Models;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// What needs the user across the server (<see cref="IProjectManager.GetAttention"/>), its push when
/// that changes, and that it is the same after a restart.
/// </summary>
public class AttentionTests
{
    private const string Question = "Which branch should I push to?";

    /// <summary>A turn that ends on a question in plain text, then waits for the answer.</summary>
    private static FakeScript Asking(string question = Question) =>
        new FakeScript().EmitInit().AwaitStdin().EmitAssistant(question).Sleep(50).EmitResult(question).AwaitStdin();

    /// <summary>A turn that ends with <paramref name="result"/>.</summary>
    private static FakeScript Finishing(string result) =>
        new FakeScript().EmitInit().AwaitStdin().EmitAssistant("Working on it.").Sleep(50).EmitResult(result);

    private static PermissionPromptRequest Bash(string command) =>
        new("Bash", JsonSerializer.SerializeToElement(new { command }), "toolu_1");

    /// <summary>Waits until <c>AttentionChanged</c> has been pushed <paramref name="count"/> times; returns the last list.</summary>
    private static async Task<IReadOnlyList<AttentionItem>> WaitForAttentionPushAsync(LifecycleHarness harness, int count)
    {
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.Hub.AttentionPushes.Count >= count), null,
            () => $"AttentionChanged was pushed {harness.Hub.AttentionPushes.Count} times, not {count}: " +
                  string.Join(" | ", harness.Hub.AttentionPushes.Select(Describe)));
        return harness.Hub.AttentionPushes[count - 1];
    }

    private static string Describe(IReadOnlyList<AttentionItem> items) =>
        "[" + string.Join(", ", items.Select(i => $"{i.ProjectId}:{i.Kind}")) + "]";

    /// <summary>Waits until the project's init line is handled, so it cannot land after a permission request and set Running over it.</summary>
    private static Task WaitForInitAsync(LifecycleHarness harness, string projectId) =>
        LifecycleHarness.WaitUntilAsync(async () => (await harness.Projects.GetStatusAsync(projectId)).OutputOffset > 0, null,
            () => $"the init line of {projectId} was not handled.\n{harness.Describe(projectId)}");

    /// <summary>The acceptance case: one project asking, one waiting on a permission.</summary>
    [Fact]
    public async Task QuestionAndPermission_AreListedOldestFirst_AndEachTransitionIsPushed()
    {
        await using var harness = new LifecycleHarness(Asking());
        var asking = await harness.CreateProjectAsync("asking");
        await harness.WaitForStateAsync(asking.Id, ProjectState.WaitingInput);
        var first = await WaitForAttentionPushAsync(harness, 1);

        harness.UseScript(new FakeScript().EmitInit().AwaitStdin());
        var permitting = await harness.CreateProjectAsync("permitting");
        await harness.WaitForStdinAsync(permitting.Id);
        await WaitForInitAsync(harness, permitting.Id);
        var request = harness.Projects.RequestPermissionAsync(permitting.Id, Bash("git push origin feature/12-x"), CancellationToken.None);
        var second = await WaitForAttentionPushAsync(harness, 2);

        var attention = harness.Projects.GetAttention();
        Assert.Equal([asking.Id, permitting.Id], attention.Select(i => i.ProjectId));
        Assert.Equal([AttentionKind.Question, AttentionKind.Permission], attention.Select(i => i.Kind));
        Assert.True(attention[0].Since < attention[1].Since);
        Assert.Equal(Question, attention[0].Text);
        Assert.Null(attention[0].Question);
        Assert.Equal("Bash: git push origin feature/12-x", attention[1].Text);
        Assert.Equal("asking", attention[0].ProjectName);
        Assert.Equal(LifecycleHarness.ProfileName, attention[1].Profile);
        Assert.Equal(LifecycleHarness.RootName, attention[1].Root);
        var permission = attention[1].Permission!;
        Assert.Equal((await harness.Projects.GetStatusAsync(permitting.Id)).PendingPermission!.RequestId, permission.RequestId);

        await harness.Projects.RespondToPermissionAsync(permitting.Id, permission.RequestId, new PermissionDecision(true));
        await request.WaitAsync(LifecycleHarness.DefaultTimeout);
        var third = await WaitForAttentionPushAsync(harness, 3);

        Assert.Equal(["[asking:Question]", "[asking:Question, permitting:Permission]", "[asking:Question]"],
            new[] { first, second, third }.Select(items => Describe(items).Replace($"{LifecycleHarness.ProfileName}/{LifecycleHarness.RootName}/", "")));
        Assert.Equal(3, harness.Hub.AttentionPushes.Count);
    }

    /// <summary>A status push that leaves the list as it was pushes no list: a burst of changes does not flood clients.</summary>
    [Fact]
    public async Task StatusChanges_ThatLeaveTheListAsItWas_PushNoList()
    {
        await using var harness = new LifecycleHarness(Asking());
        var asking = await harness.CreateProjectAsync("asking");
        await harness.WaitForStateAsync(asking.Id, ProjectState.WaitingInput);
        await WaitForAttentionPushAsync(harness, 1);
        var statusPushes = harness.Hub.StatusPushes(asking.Id).Count;

        for (var i = 0; i < 5; i++)
            await harness.Projects.UpdateCustomStatusAsync(asking.Id, $"step {i}");

        Assert.Equal(statusPushes + 5, harness.Hub.StatusPushes(asking.Id).Count);
        Assert.Single(harness.Hub.AttentionPushes);
    }

    [Fact]
    public async Task AskUserQuestion_IsAQuestion_WithItsOptions()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(created.Id);
        await WaitForInitAsync(harness, created.Id);
        var input = JsonSerializer.SerializeToElement(new
        {
            questions = new[] { new { question = "Which color?", header = "Color", options = new[] { new { label = "Red" }, new { label = "Blue" } }, multiSelect = false } },
        });

        _ = harness.Projects.RequestPermissionAsync(created.Id, new PermissionPromptRequest("AskUserQuestion", input, "toolu_q"), CancellationToken.None);
        var pushed = Assert.Single(await WaitForAttentionPushAsync(harness, 1));

        Assert.Equal(AttentionKind.Question, pushed.Kind);
        Assert.Equal("Which color?", pushed.Text);
        Assert.Null(pushed.Permission);
        Assert.Equal(["Red", "Blue"], pushed.Question!.Questions[0].Options.Select(o => o.Label));
        Assert.Equal(pushed.Question.RequestedAt, pushed.Since);
    }

    [Fact]
    public async Task ProcessFailing_IsAnError_WithWhatItWrote()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin().Stderr("fatal: not a git repository").Exit(1));
        var created = await harness.CreateProjectAsync();
        var failed = await harness.WaitForStateAsync(created.Id, ProjectState.Error);

        var item = Assert.Single(await WaitForAttentionPushAsync(harness, 1));
        Assert.Equal(AttentionKind.Error, item.Kind);
        Assert.Contains("fatal: not a git repository", item.Text);
        Assert.Equal(failed.UpdatedAt, item.Since);
    }

    /// <summary>A result is Finished until seen, and both that and when it came survive a restart.</summary>
    [Fact]
    public async Task Result_IsFinishedUntilSeen_AcrossRestarts()
    {
        await using var harness = new LifecycleHarness(Finishing("Pushed feature/12-x and opened a pull request."));
        var created = await harness.CreateProjectAsync();
        var idle = await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        Assert.Equal("Pushed feature/12-x and opened a pull request.", idle.LastResult);

        var finished = Assert.Single(await WaitForAttentionPushAsync(harness, 1));
        Assert.Equal(AttentionKind.Finished, finished.Kind);
        Assert.Equal("Pushed feature/12-x and opened a pull request.", finished.Text);
        Assert.Equal(idle.LastResultAt, finished.Since);

        await harness.RestartAsync();

        Assert.Equal(ProjectState.Stopped, (await harness.Projects.GetStatusAsync(created.Id)).State);
        Assert.Equal(finished, Assert.Single(harness.Projects.GetAttention()));

        await harness.Projects.MarkSeenAsync(created.Id);

        Assert.Empty(harness.Projects.GetAttention());
        Assert.Empty(harness.Hub.AttentionPushes[^1]);
        Assert.NotNull(harness.ReadStatusFile(created.Id).SeenAt);
        await harness.RestartAsync();
        Assert.Empty(harness.Projects.GetAttention());
    }

    /// <summary>A reply is the user having seen the result: the next result is Finished again.</summary>
    [Fact]
    public async Task Reply_SeesTheResult_AndTheNextResultIsFinishedAgain()
    {
        await using var harness = new LifecycleHarness(Finishing("First done.").AwaitStdin().Sleep(200).EmitResult("Second done."));
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        await WaitForAttentionPushAsync(harness, 1);

        await harness.Projects.SendInputAsync(created.Id, "Now the second part");

        Assert.Empty(await WaitForAttentionPushAsync(harness, 2));
        var next = Assert.Single(await WaitForAttentionPushAsync(harness, 3));
        Assert.Equal(AttentionKind.Finished, next.Kind);
        Assert.Equal("Second done.", next.Text);
    }

    /// <summary>A question claude was waiting on when the server stopped still asks after the restart, since the same time.</summary>
    [Fact]
    public async Task Question_StillAsks_AfterARestart()
    {
        await using var harness = new LifecycleHarness(Asking());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.WaitingInput);
        var before = Assert.Single(await WaitForAttentionPushAsync(harness, 1));

        await harness.RestartAsync();

        Assert.Equal(before, Assert.Single(harness.Projects.GetAttention()));
    }

    /// <summary>A resume the root config refuses makes the project Error, and that is pushed: status and list.</summary>
    [Fact]
    public async Task ResumeRefusedByTheRootConfig_PushesTheError()
    {
        await using var harness = new LifecycleHarness(Finishing("Done."));
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        await harness.Projects.StopProjectAsync(created.Id);
        var finished = await WaitForAttentionPushAsync(harness, 1);
        Assert.Equal(AttentionKind.Finished, Assert.Single(finished).Kind);
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "config.json"), "{ not json");

        await Assert.ThrowsAsync<LaunchConfigException>(() => harness.Projects.ReplyAndResumeAsync(created.Id, "go on"));

        Assert.Equal(ProjectState.Error, harness.Hub.StatusPushes(created.Id)[^1].State);
        var error = Assert.Single(await WaitForAttentionPushAsync(harness, 2));
        Assert.Equal(AttentionKind.Error, error.Kind);
        Assert.StartsWith("root config unreadable", error.Text);
        Assert.Equal(2, harness.Hub.AttentionPushes.Count);
    }

    /// <summary>Deleting a profile with its contents removes its projects from the list, and says so.</summary>
    [Fact]
    public async Task DeletingAProfileWithItsContents_PushesTheListWithoutItsProjects()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin().Stderr("boom").Exit(1));
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Error);
        Assert.Single(await WaitForAttentionPushAsync(harness, 1));

        await harness.Projects.DeleteProfileAsync(LifecycleHarness.ProfileName, deleteContents: true);

        Assert.Empty(await WaitForAttentionPushAsync(harness, 2));
        Assert.Empty(harness.Projects.GetAttention());
        Assert.Equal(2, harness.Hub.AttentionPushes.Count);
    }

    /// <summary>Texts are plain, for a phone or a voice: code blocks go, and they are cut to about 500 characters.</summary>
    [Fact]
    public void Text_IsPlain_AndShort()
    {
        var text = "Run this:\n```bash\ngit push --force\n```\nthen `check` it.  " + string.Join(" ", Enumerable.Repeat("word", 200));

        var plain = Attention.PlainText(text);

        Assert.StartsWith("Run this: (code) then check it. word word", plain);
        Assert.DoesNotContain("git push", plain);
        Assert.DoesNotContain("`", plain);
        Assert.EndsWith("word…", plain);
        Assert.InRange(plain.Length, 400, Attention.MaxTextLength);
    }
}

/// <summary>
/// <see cref="IProjectManager.ReplyAndResumeAsync"/>: one call answers a project whether its claude
/// runs or not. Delivery is asserted on what the fake read from its stdin.
/// </summary>
public class ReplyAndResumeTests
{
    private const string Reply = "carry on with the tests";

    /// <summary>A project whose first turn is over and that has been stopped.</summary>
    private static async Task<ProjectStatus> StoppedAsync(LifecycleHarness harness)
    {
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        await harness.Projects.StopProjectAsync(created.Id);
        await harness.WaitForStateAsync(created.Id, ProjectState.Stopped);
        return created;
    }

    /// <summary>As the real CLI does: nothing, not even system/init, until the first input arrives.</summary>
    private static FakeScript ResumedTurn(string answer) =>
        new FakeScript().AwaitStdin().EmitInit().EmitAssistant(answer).Sleep(50).EmitResult(answer);

    private static bool Carries(string stdinLine, string text) => stdinLine.Contains(JsonSerializer.Serialize(text));

    [Fact]
    public async Task Stopped_IsResumed_AndTheReplyDelivered()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().Turn("First."));
        var stopped = await StoppedAsync(harness);
        harness.UseScript(ResumedTurn("Second."));

        await harness.Projects.ReplyAndResumeAsync(stopped.Id, Reply);

        var resumed = harness.Launches(stopped.Id)[1];
        Assert.NotNull(resumed.ArgValue("--resume"));
        Assert.True(Carries(Assert.Single(resumed.Stdin), Reply), $"the resumed launch read: {string.Join(" | ", resumed.Stdin)}");
        var idle = await harness.WaitForStateAsync(stopped.Id, ProjectState.Idle);
        Assert.Equal("Second.", idle.LastResult);
    }

    /// <summary>With claude running it is SendInput: a pending permission is denied with the reply, which does not reach stdin.</summary>
    [Fact]
    public async Task Running_WithAPendingPermission_DeniesItWithTheReply()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(created.Id);
        await LifecycleHarness.WaitUntilAsync(async () => (await harness.Projects.GetStatusAsync(created.Id)).OutputOffset > 0, null,
            () => harness.Describe(created.Id));
        var asking = harness.Projects.RequestPermissionAsync(created.Id,
            new PermissionPromptRequest("Bash", JsonSerializer.SerializeToElement(new { command = "rm -rf build" }), "toolu_1"), CancellationToken.None);
        await harness.WaitForStatusPushAsync(created.Id, s => s.PendingPermission != null);

        await harness.Projects.ReplyAndResumeAsync(created.Id, "keep the build folder");

        var result = await asking.WaitAsync(LifecycleHarness.DefaultTimeout);
        Assert.Equal("deny", result.Behavior);
        Assert.Contains("keep the build folder", result.Message);
        Assert.Single(harness.Launches(created.Id));
        Assert.Single(harness.Launches(created.Id)[0].Stdin);
    }

    [Fact]
    public async Task Running_Idle_IsANewTurn_WithoutAResume()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().Turn("First.").Turn("Second."));
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);

        await harness.Projects.ReplyAndResumeAsync(created.Id, Reply);

        var launch = await harness.WaitForStdinAsync(created.Id, count: 2);
        Assert.True(Carries(launch.Stdin[1], Reply));
        Assert.Single(harness.Launches(created.Id));
    }

    /// <summary>claude exits before its session starts: the call fails saying why, rather than hanging, and the project is Error.</summary>
    [Fact]
    public async Task ResumedProcessExiting_BeforeItsSessionStarts_FailsWithItsError()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().Turn("First."));
        var stopped = await StoppedAsync(harness);
        harness.UseScript(new FakeScript().AwaitStdin().Stderr("Error: invalid API key").Exit(1));

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Projects.ReplyAndResumeAsync(stopped.Id, Reply).WaitAsync(LifecycleHarness.DefaultTimeout));

        Assert.Contains("invalid API key", failed.Message);
        var status = await harness.WaitForStateAsync(stopped.Id, ProjectState.Error);
        Assert.Contains("invalid API key", status.LastError);
    }

    [Fact]
    public async Task ResumedProcess_NotStartingItsSessionInTime_FailsWithATimeout()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().Turn("First."),
            settings: new Dictionary<string, string?> { [ProjectManager.SessionStartTimeoutSetting] = "1" });
        var stopped = await StoppedAsync(harness);
        harness.UseScript(new FakeScript().AwaitStdin());

        var timedOut = await Assert.ThrowsAsync<TimeoutException>(() =>
            harness.Projects.ReplyAndResumeAsync(stopped.Id, Reply).WaitAsync(LifecycleHarness.DefaultTimeout));

        Assert.Contains("did not start its session within 1 seconds", timedOut.Message);
        Assert.True(Carries(Assert.Single(harness.Launches(stopped.Id)[1].Stdin), Reply));
    }

    /// <summary>
    /// claude has no conversation for the session: the resume exits and a fresh session takes its
    /// place. The reply reaches the fresh one, once.
    /// </summary>
    [Fact]
    public async Task ResumeWithoutAConversation_DeliversTheReplyToTheFreshSession()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().Turn("First."));
        var stopped = await StoppedAsync(harness);
        harness.UseScript(new FakeScript().RejectResume().AwaitStdin().EmitInit().AwaitStdin().EmitAssistant("On it.").Sleep(50).EmitResult());

        await harness.Projects.ReplyAndResumeAsync(stopped.Id, Reply);

        var fresh = await harness.WaitForLaunchAsync(stopped.Id, l => l.Stdin.Any(line => Carries(line, Reply)), index: 2);
        Assert.Null(fresh.ArgValue("--resume"));
        Assert.Single(fresh.Stdin, line => Carries(line, Reply));
        await harness.WaitForStateAsync(stopped.Id, ProjectState.Idle);
    }
}
