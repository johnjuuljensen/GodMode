using GodMode.FakeClaude;
using GodMode.Server.Models;
using GodMode.Shared.Enums;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A resumed project runs as its create launched it: the same arguments, environment (the
/// profile's included), MCP servers and bridge, except for <c>--resume</c> in place of
/// <c>--session-id</c> and the project token, which every launch is issued afresh.
/// </summary>
public class ResumeLaunchTests
{
    private const string ConfigDir = "CLAUDE_CONFIG_DIR";

    /// <summary>The per-launch values: the session flag's pair and the project token.</summary>
    private static readonly string[] SessionFlags = ["--session-id", "--resume"];

    [Fact]
    public async Task Resume_LaunchesWithTheCreatesArgumentsAndEnvironment_ExceptTheSessionFlagAndToken()
    {
        var configDir = Path.Combine(Path.GetTempPath(), "godmode-claude-config");
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin(),
            profileEnvironment: new Dictionary<string, string> { [ConfigDir] = configDir });
        var created = await harness.CreateProjectAsync();
        var create = await harness.WaitForStdinAsync(created.Id);
        var createMcp = GodModeMcpEntry.Of(create);

        await harness.Projects.StopProjectAsync(created.Id);
        await harness.WaitForStateAsync(created.Id, ProjectState.Stopped);
        await harness.Projects.ResumeProjectAsync(created.Id);
        var resume = await harness.WaitForLaunchAsync(created.Id, _ => true, index: 1);
        var resumeMcp = GodModeMcpEntry.Of(resume);

        Assert.Equal(configDir, create.Environment[ConfigDir]);
        Assert.Equal(create.ArgValue("--session-id"), resume.ArgValue("--resume"));
        Assert.Equal(WithoutSessionFlag(create.Argv), WithoutSessionFlag(resume.Argv));
        Assert.Equal(Sorted(create.Environment), Sorted(resume.Environment));
        Assert.NotEqual(createMcp.Token, resumeMcp.Token);
        Assert.Equal(createMcp.WithoutToken(), resumeMcp.WithoutToken());
    }

    /// <summary>
    /// The session is the one claude reports in its <c>system/init</c>, not the one the server
    /// asked for: a resume names it, and a restarted server reads it back from session-id.
    /// </summary>
    [Fact]
    public async Task Resume_NamesTheSessionClaudeReportedInItsInit()
    {
        const string reported = "3f2b1c9e-0000-4000-8000-00000000abcd";
        await using var harness = new LifecycleHarness(new FakeScript()
            .Emit($$"""{"type":"system","subtype":"init","session_id":"{{reported}}"}""")
            .AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(created.Id);
        var sessionFile = Path.Combine(harness.ProjectPath(created.Id), ".godmode", "session-id");
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(File.ReadAllText(sessionFile) == reported), null,
            () => $"session-id is {File.ReadAllText(sessionFile)}, not the {reported} claude reported.\n{harness.Describe(created.Id)}");

        await harness.Projects.StopProjectAsync(created.Id);
        await harness.WaitForStateAsync(created.Id, ProjectState.Stopped);
        await harness.Projects.ResumeProjectAsync(created.Id);

        var resume = await harness.WaitForLaunchAsync(created.Id, _ => true, index: 1);
        Assert.Equal(reported, resume.ArgValue("--resume"));
    }

    /// <summary>
    /// A resume that cannot launch with the project's own action refuses: the project is Error,
    /// saying why, and nothing is launched. The default action would drop the action's environment
    /// (an account's CLAUDE_CONFIG_DIR, say), and --resume would then miss in the wrong account.
    /// </summary>
    [Theory]
    [InlineData("config.json", "{ not json", "root config unreadable")]
    // An overlay makes the root's actions its own: the project's "Create" is gone
    [InlineData("config.other.json", "{}", "root config has no action 'Create'")]
    public async Task Resume_WithoutTheProjectsActionInItsRootConfig_IsErrorAndLaunchesNothing(string file, string content, string reason)
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(created.Id);
        await harness.Projects.StopProjectAsync(created.Id);
        await harness.WaitForStateAsync(created.Id, ProjectState.Stopped);
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", file), content);

        var refused = await Assert.ThrowsAsync<LaunchConfigException>(() => harness.Projects.ResumeProjectAsync(created.Id));

        Assert.StartsWith(reason, refused.Message);
        var status = await harness.Projects.GetStatusAsync(created.Id);
        Assert.Equal(ProjectState.Error, status.State);
        Assert.StartsWith(reason, status.LastError);
        Assert.Equal(ProjectState.Error, harness.ReadStatusFile(created.Id).State);
        Assert.Single(harness.Launches(created.Id));
    }

    private static IReadOnlyList<string> WithoutSessionFlag(IReadOnlyList<string> argv) =>
        argv.Where((_, i) => !SessionFlags.Contains(argv[i]) && (i == 0 || !SessionFlags.Contains(argv[i - 1]))).ToList();

    private static SortedDictionary<string, string> Sorted(IReadOnlyDictionary<string, string> environment) =>
        new(environment.ToDictionary(e => e.Key, e => e.Value), StringComparer.Ordinal);
}
