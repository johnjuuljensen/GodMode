using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Shared;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A project's <c>.godmode</c> files are its session's to write, so what a restarted server reads
/// back from them is checked before it reaches the <c>claude</c> command line or a link.
/// </summary>
public class UntrustedProjectStateTests
{
    /// <summary>
    /// A session id that is not a GUID is no session: the resume starts a fresh one, on a new
    /// GUID, and the saved value never reaches the command line, where it would read as a flag.
    /// </summary>
    [Theory]
    [InlineData("--settings=x")]
    [InlineData("not-a-session")]
    public async Task SessionIdThatIsNoGuid_IsResumedWithAFreshSession_AndNeverReachesTheCommandLine(string saved)
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(created.Id);
        await harness.Projects.StopProjectAsync(created.Id);
        await harness.WaitForStateAsync(created.Id, ProjectState.Stopped);
        harness.StopHost();
        var sessionFile = Path.Combine(harness.ProjectPath(created.Id), ".godmode", "session-id");
        File.WriteAllText(sessionFile, saved);

        await harness.RestartAsync();
        await harness.Projects.ResumeProjectAsync(created.Id);
        var resume = await harness.WaitForLaunchAsync(created.Id, _ => true, index: 1);

        Assert.DoesNotContain(resume.Argv, arg => arg.Contains(saved));
        Assert.DoesNotContain("--settings", resume.Argv);
        Assert.Null(resume.ArgValue("--resume"));
        var fresh = resume.ArgValue("--session-id");
        Assert.True(Guid.TryParseExact(fresh, "D", out _), $"the fresh session is '{fresh}', not a GUID");
        Assert.NotEqual(harness.Launches(created.Id)[0].ArgValue("--session-id"), fresh);
        Assert.Equal(fresh, harness.ReadSessionIdFile(created.Id));
    }

    /// <summary>
    /// A pull request link in a recovered status.json is kept only if the status script's output
    /// could have set it: an http(s) URL. One that is not is dropped, with its pull request.
    /// </summary>
    [Theory]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("https://example.test/pulls/7", true)]
    public async Task RecoveredPullRequestUrl_IsKeptOnlyIfHttp(string url, bool kept)
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(created.Id);
        await harness.Projects.StopProjectAsync(created.Id);
        await harness.WaitForStateAsync(created.Id, ProjectState.Stopped);
        harness.StopHost();
        var statusFile = Path.Combine(harness.ProjectPath(created.Id), ".godmode", "status.json");
        var pullRequest = new PullRequestStatus(url, 7, PullRequestState.Open, PullRequestReview.None, DateTime.UtcNow);
        File.WriteAllText(statusFile, JsonSerializer.Serialize(harness.ReadStatusFile(created.Id) with { PullRequest = pullRequest }, JsonDefaults.Options));

        await harness.RestartAsync(resume: false);

        var recovered = (await harness.Projects.GetStatusAsync(created.Id)).PullRequest;
        if (kept) Assert.Equal(url, recovered?.Url);
        else Assert.Null(recovered);
    }
}
