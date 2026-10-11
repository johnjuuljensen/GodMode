using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A project's pull request, from its root's status script (<see cref="ProjectStatus.PullRequest"/>): run
/// on a transition to Idle or Stopped and polled while open, and a changes-requested review in attention.
/// The script here prints whatever <c>{folder}.json</c> in the root holds for the folder it runs in, else
/// <c>pr.json</c>, and records the folder it ran in.
/// </summary>
public class PullRequestTests
{
    private const string Url = "https://example.test/pulls/7";
    private const string Result = "Opened the pull request.";

    private const string StatusScript = """
        $ErrorActionPreference = 'Stop'
        Add-Content -Path (Join-Path $env:GODMODE_ROOT_PATH 'status-runs.txt') -Value (Split-Path -Leaf (Get-Location).Path)
        $report = Join-Path $env:GODMODE_ROOT_PATH ((Split-Path -Leaf (Get-Location).Path) + '.json')
        if (-not (Test-Path $report)) { $report = Join-Path $env:GODMODE_ROOT_PATH 'pr.json' }
        if (Test-Path $report) { Get-Content -Raw $report } else { '{}' }
        """;

    private static FakeScript Finishing() =>
        new FakeScript().EmitInit().AwaitStdin().EmitAssistant("Working on it.").Sleep(50).EmitResult(Result);

    /// <param name="pollSeconds">How often an open pull request is checked.</param>
    /// <param name="statusScript">Whether the root names the status script in its config.</param>
    private static LifecycleHarness Harness(double pollSeconds = 1, bool statusScript = true)
    {
        var harness = new LifecycleHarness(Finishing(),
            rootConfig: statusScript ? new Dictionary<string, object> { ["status"] = "scripts/status" } : null,
            settings: new Dictionary<string, string?>
            {
                [ProjectManager.PullRequestPollSetting] = pollSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
        // There with or without the config naming it: a root that does not name it does not run it
        UseStatusScript(harness, StatusScript);
        return harness;
    }

    private static void UseStatusScript(LifecycleHarness harness, string script)
    {
        var scripts = Path.Combine(harness.RootPath, ".godmode-root", "scripts");
        Directory.CreateDirectory(scripts);
        GodMode.ProjectFiles.AtomicFile.WriteAllText(Path.Combine(scripts, "status.ps1"), script);
    }

    private static void Report(LifecycleHarness harness, string state, string review) =>
        WriteReport(harness, $$$"""{"pullRequest": {"url": "{{{Url}}}", "number": 7, "state": "{{{state}}}", "review": "{{{review}}}"}}""");

    /// <summary>
    /// Replaces the report whole: the script may be reading it as it changes, and a report written in
    /// place can be read empty or half written.
    /// </summary>
    private static void WriteReport(LifecycleHarness harness, string output) => WriteReport(harness, "pr", output);

    /// <summary>The report for the project in <paramref name="folder"/> alone.</summary>
    private static void WriteReport(LifecycleHarness harness, string folder, string output) =>
        GodMode.ProjectFiles.AtomicFile.WriteAllText(Path.Combine(harness.RootPath, folder + ".json"), output);

    /// <summary>
    /// The folders the script ran in, read so that a script appending to the file is not refused
    /// (it would fail, and its failure is a warning). A read while one appends is tried again.
    /// </summary>
    private static string[] Runs(LifecycleHarness harness)
    {
        var path = Path.Combine(harness.RootPath, "status-runs.txt");
        for (var attempt = 1; ; attempt++)
        {
            try { return File.Exists(path) ? LifecycleHarness.ReadShared(path).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries) : []; }
            catch (IOException) when (attempt < 50) { Thread.Sleep(20); }
        }
    }

    private static async Task<PullRequestStatus> WaitForPullRequestAsync(LifecycleHarness harness, string projectId,
        PullRequestState state, PullRequestReview review)
    {
        PullRequestStatus? pr = null;
        await LifecycleHarness.WaitUntilAsync(async () =>
                (pr = (await harness.Projects.GetStatusAsync(projectId)).PullRequest) is { } found && found.State == state && found.Review == review,
            null, () => $"the pull request did not become {state}/{review}; it is {pr?.State}/{pr?.Review}.\n{harness.Describe(projectId)}");
        return pr!;
    }

    private static Task WaitForWarningAsync(LifecycleHarness harness, string text) =>
        LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.Warnings.Any(w => w.Contains(text))), null,
            () => $"no warning with '{text}': {string.Join("\n", harness.Warnings)}");

    /// <summary>The acceptance case: the script prints each state in turn, and the status and attention follow.</summary>
    [Fact]
    public async Task EachState_TheScriptPrints_IsTheProjectsPullRequest_AndChangesRequestedIsAReview()
    {
        await using var harness = Harness();
        Report(harness, "draft", "none");
        var created = await harness.CreateProjectAsync();

        // The turn's end ran it, in the project folder
        var draft = await WaitForPullRequestAsync(harness, created.Id, PullRequestState.Draft, PullRequestReview.None);
        Assert.Equal(new PullRequestStatus(Url, 7, PullRequestState.Draft, PullRequestReview.None, draft.ChangedAt), draft);
        Assert.Equal(Path.GetFileName(harness.ProjectPath(created.Id)), Runs(harness)[0]);
        // Saved just after it changed in memory, which is what the wait saw
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.ReadStatusFile(created.Id).PullRequest == draft), null,
            () => $"status.json has pull request {harness.ReadStatusFile(created.Id).PullRequest}, not {draft}");
        var finished = Assert.Single(harness.Projects.GetAttention());
        Assert.Equal((AttentionKind.Finished, Result, Url), (finished.Kind, finished.Text, finished.PullRequestUrl));

        Report(harness, "open", "none");
        await WaitForPullRequestAsync(harness, created.Id, PullRequestState.Open, PullRequestReview.None);

        Report(harness, "open", "changes_requested");
        var changes = await WaitForPullRequestAsync(harness, created.Id, PullRequestState.Open, PullRequestReview.ChangesRequested);
        var review = Assert.Single(harness.Projects.GetAttention());
        Assert.Equal((AttentionKind.Review, "Changes requested on pull request #7.", Url, changes.ChangedAt),
            (review.Kind, review.Text, review.PullRequestUrl, review.Since));
        await LifecycleHarness.WaitUntilAsync(
            () => Task.FromResult(harness.Hub.AttentionPushes.LastOrDefault() is [{ Kind: AttentionKind.Review }]), null,
            () => "the review was not pushed");

        Report(harness, "open", "approved");
        await WaitForPullRequestAsync(harness, created.Id, PullRequestState.Open, PullRequestReview.Approved);
        Assert.Equal(AttentionKind.Finished, Assert.Single(harness.Projects.GetAttention()).Kind);

        Report(harness, "merged", "approved");
        await WaitForPullRequestAsync(harness, created.Id, PullRequestState.Merged, PullRequestReview.Approved);

        // Merged: no more polls
        var runs = Runs(harness).Length;
        Report(harness, "closed", "approved");
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(runs, Runs(harness).Length);
        Assert.Equal(PullRequestState.Merged, (await harness.Projects.GetStatusAsync(created.Id)).PullRequest!.State);

        var pushed = harness.Hub.StatusPushes(created.Id).Select(s => s.PullRequest).OfType<PullRequestStatus>()
            .Select(pr => $"{pr.State}/{pr.Review}").Distinct();
        Assert.Equal(["Draft/None", "Open/None", "Open/ChangesRequested", "Open/Approved", "Merged/Approved"], pushed);
        Assert.Empty(harness.Warnings);
    }

    /// <summary>A root that names no status script behaves as before: nothing runs, the status has no pull request.</summary>
    [Fact]
    public async Task RootWithoutAStatusScript_RunsNothing_AndHasNoPullRequest()
    {
        await using var harness = Harness(statusScript: false);
        Report(harness, "open", "changes_requested");
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.Empty(Runs(harness));
        Assert.Null((await harness.Projects.GetStatusAsync(created.Id)).PullRequest);
        Assert.Null(harness.ReadStatusFile(created.Id).PullRequest);
        var item = Assert.Single(harness.Projects.GetAttention());
        Assert.Equal((AttentionKind.Finished, null), (item.Kind, item.PullRequestUrl));
        Assert.All(harness.Hub.StatusPushes(created.Id), status => Assert.Null(status.PullRequest));
        Assert.Empty(harness.Warnings);
    }

    /// <summary>Output that is not the documented JSON, a failing script and a slow one each change nothing; polling goes on.</summary>
    [Fact]
    public async Task Failures_AreLogged_AndLeaveThePullRequestAsItWas()
    {
        await using var harness = Harness();
        Report(harness, "open", "changes_requested");
        var created = await harness.CreateProjectAsync();
        var known = await WaitForPullRequestAsync(harness, created.Id, PullRequestState.Open, PullRequestReview.ChangesRequested);

        WriteReport(harness, "Running gh pr view... " + new string('x', 100));
        await WaitForWarningAsync(harness, "not one JSON object");
        Assert.Equal(known, (await harness.Projects.GetStatusAsync(created.Id)).PullRequest);

        UseStatusScript(harness, "[Console]::Error.WriteLine('gh: could not reach github.com'); exit 1");
        await WaitForWarningAsync(harness, "gh: could not reach github.com");
        Assert.Equal(known, (await harness.Projects.GetStatusAsync(created.Id)).PullRequest);

        // The timeout is short for the slow script alone, set before it is swapped in so no run of it has the ceiling:
        // a fast one may take seconds to start on a loaded machine
        harness.Configure(ProjectManager.StatusScriptTimeoutSetting, TimeSpan.FromSeconds(3));
        UseStatusScript(harness, "Start-Sleep -Seconds 60; '{}'");
        await WaitForWarningAsync(harness, "it took longer than 3s");
        Assert.Equal(known, (await harness.Projects.GetStatusAsync(created.Id)).PullRequest);
        Assert.Equal(AttentionKind.Review, Assert.Single(harness.Projects.GetAttention()).Kind);

        UseStatusScript(harness, StatusScript);
        harness.Configure(ProjectManager.StatusScriptTimeoutSetting, TestTimeouts.Script);
        Report(harness, "open", "approved");
        await WaitForPullRequestAsync(harness, created.Id, PullRequestState.Open, PullRequestReview.Approved);
    }

    /// <summary>The pull request and when it changed are in status.json: a restart checks an open one again, and the review keeps its time.</summary>
    [Fact]
    public async Task Restart_ChecksAnOpenPullRequest_AndTheReviewKeepsItsTime()
    {
        await using var harness = Harness(pollSeconds: 600);
        Report(harness, "open", "changes_requested");
        var created = await harness.CreateProjectAsync();
        var before = await WaitForPullRequestAsync(harness, created.Id, PullRequestState.Open, PullRequestReview.ChangesRequested);
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        // The check Idle made is over; there are no polls
        await Task.Delay(TimeSpan.FromSeconds(3));
        var runs = Runs(harness).Length;

        await harness.RestartAsync();
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(Runs(harness).Length > runs), null, () => "recovery ran no check");
        await Task.Delay(500);

        Assert.Equal(before, (await harness.Projects.GetStatusAsync(created.Id)).PullRequest);
        var review = Assert.Single(harness.Projects.GetAttention());
        Assert.Equal((AttentionKind.Review, before.ChangedAt), (review.Kind, review.Since));
    }

    /// <summary>Seeing a review clears it until the pull request changes again.</summary>
    [Fact]
    public async Task MarkSeen_ClearsTheReview_UntilItChangesAgain()
    {
        await using var harness = Harness();
        Report(harness, "open", "changes_requested");
        var created = await harness.CreateProjectAsync();
        await WaitForPullRequestAsync(harness, created.Id, PullRequestState.Open, PullRequestReview.ChangesRequested);

        await harness.Projects.MarkSeenAsync(created.Id);
        Assert.Empty(harness.Projects.GetAttention());

        Report(harness, "open", "none");
        await WaitForPullRequestAsync(harness, created.Id, PullRequestState.Open, PullRequestReview.None);
        Report(harness, "open", "changes_requested");
        var again = await WaitForPullRequestAsync(harness, created.Id, PullRequestState.Open, PullRequestReview.ChangesRequested);
        var review = Assert.Single(harness.Projects.GetAttention());
        Assert.Equal((AttentionKind.Review, again.ChangedAt), (review.Kind, review.Since));
    }

    private static string Progress(int resolved, int total, string state = "draft") =>
        $$$"""{"pullRequest": {"url": "{{{Url}}}", "number": 7, "state": "{{{state}}}", "review": "none"}, "progress": {"resolved": {{{resolved}}}, "total": {{{total}}}}}""";

    private static async Task<WorkProgress?> WaitForProgressAsync(LifecycleHarness harness, string projectId, WorkProgress? expected)
    {
        WorkProgress? progress = null;
        await LifecycleHarness.WaitUntilAsync(async () => (progress = (await harness.Projects.GetStatusAsync(projectId)).Progress) == expected,
            null, () => $"the progress did not become {expected}; it is {progress}.\n{harness.Describe(projectId)}");
        return progress;
    }

    /// <summary>
    /// The script's progress (issue #545) is the project's, saved and pushed beside its pull request; one the server
    /// does not keep fails the check and leaves both as they were; a report without one clears it.
    /// </summary>
    [Fact]
    public async Task Progress_TheScriptPrints_IsTheProjects_AndABadOneLeavesTheStatusAsItWas()
    {
        await using var harness = Harness();
        WriteReport(harness, Progress(1, 3));
        var created = await harness.CreateProjectAsync();
        await WaitForProgressAsync(harness, created.Id, new WorkProgress(1, 3));
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.ReadStatusFile(created.Id).Progress == new WorkProgress(1, 3)), null,
            () => $"status.json has progress {harness.ReadStatusFile(created.Id).Progress}");
        var known = await harness.Projects.GetStatusAsync(created.Id);
        Assert.Equal(PullRequestState.Draft, known.PullRequest!.State);

        // More resolved than there are, with the pull request open: neither changes
        WriteReport(harness, Progress(4, 3, state: "open"));
        await WaitForWarningAsync(harness, "progress.resolved 4 is more than progress.total 3");
        var after = await harness.Projects.GetStatusAsync(created.Id);
        Assert.Equal((known.PullRequest, known.Progress), (after.PullRequest, after.Progress));

        WriteReport(harness, Progress(2, 3));
        await WaitForProgressAsync(harness, created.Id, new WorkProgress(2, 3));
        Report(harness, "draft", "none");
        await WaitForProgressAsync(harness, created.Id, null);

        var pushed = harness.Hub.StatusPushes(created.Id).Select(s => s.Progress).OfType<WorkProgress>().Distinct();
        Assert.Equal([new WorkProgress(1, 3), new WorkProgress(2, 3)], pushed);
        Assert.Equal(new WorkProgress(2, 3), harness.Hub.StatusPushes(created.Id).Select(s => s.Progress).OfType<WorkProgress>().Last());
    }

    /// <summary>
    /// A child's pull request that becomes merged checks its parent, whose progress it may have moved (issue #545),
    /// rather than leaving it to the parent's next poll.
    /// </summary>
    [Fact]
    public async Task AChildsMerge_ChecksItsParent()
    {
        await using var harness = Harness(pollSeconds: 600);
        WriteReport(harness, Progress(0, 1));
        var parent = await harness.CreateProjectAsync("parent");
        await WaitForProgressAsync(harness, parent.Id, new WorkProgress(0, 1));
        await harness.WaitForStateAsync(parent.Id, ProjectState.Idle);
        // The check Idle made is over, and the next poll is 10 minutes away
        await Task.Delay(TimeSpan.FromSeconds(2));
        var parentRuns = Runs(harness).Count(run => run == Path.GetFileName(harness.ProjectPath(parent.Id)));

        WriteReport(harness, Path.GetFileName(harness.ProjectPath(parent.Id)), Progress(1, 1));
        Report(harness, "merged", "approved");
        var child = await harness.CreateProjectAsync("child", parentId: parent.Id);
        await WaitForPullRequestAsync(harness, child.Id, PullRequestState.Merged, PullRequestReview.Approved);

        await WaitForProgressAsync(harness, parent.Id, new WorkProgress(1, 1));
        Assert.Equal(parentRuns + 1, Runs(harness).Count(run => run == Path.GetFileName(harness.ProjectPath(parent.Id))));
    }

    [Fact]
    public async Task Delete_StopsTheChecks()
    {
        await using var harness = Harness();
        Report(harness, "open", "none");
        var created = await harness.CreateProjectAsync();
        await WaitForPullRequestAsync(harness, created.Id, PullRequestState.Open, PullRequestReview.None);
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        var polled = Runs(harness).Length;
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(Runs(harness).Length > polled), null, () => "an open pull request was not polled");

        await harness.Projects.DeleteProjectAsync(created.Id);
        var runs = Runs(harness).Length;
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.Equal(runs, Runs(harness).Length);
        Assert.False(Directory.Exists(harness.ProjectPath(created.Id)));
    }
}
