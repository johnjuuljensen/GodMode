using GodMode.FakeClaude;
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
        var createMcp = McpConfig(create);

        await harness.Projects.StopProjectAsync(created.Id);
        await harness.WaitForStateAsync(created.Id, ProjectState.Stopped);
        await harness.Projects.ResumeProjectAsync(created.Id);
        var resume = await harness.WaitForLaunchAsync(created.Id, _ => true, index: 1);
        var resumeMcp = McpConfig(resume);

        Assert.Equal(configDir, create.Environment[ConfigDir]);
        Assert.Equal(create.ArgValue("--session-id"), resume.ArgValue("--resume"));
        Assert.Equal(WithoutSessionFlag(create.Argv), WithoutSessionFlag(resume.Argv));
        Assert.Equal(WithoutToken(create.Environment), WithoutToken(resume.Environment));
        Assert.NotEqual(create.Environment["GODMODE_PROJECT_TOKEN"], resume.Environment["GODMODE_PROJECT_TOKEN"]);
        Assert.Equal(createMcp, resumeMcp);
        Assert.Contains("godmode-bridge", resumeMcp);
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

    private static IReadOnlyList<string> WithoutSessionFlag(IReadOnlyList<string> argv) =>
        argv.Where((_, i) => !SessionFlags.Contains(argv[i]) && (i == 0 || !SessionFlags.Contains(argv[i - 1]))).ToList();

    private static SortedDictionary<string, string> WithoutToken(IReadOnlyDictionary<string, string> environment) =>
        new(environment.Where(e => e.Key != "GODMODE_PROJECT_TOKEN").ToDictionary(e => e.Key, e => e.Value), StringComparer.Ordinal);

    /// <summary>The MCP config the launch was given; read while it runs, since its exit deletes it.</summary>
    private static string McpConfig(FakeLaunch launch) =>
        File.ReadAllText(launch.ArgValue("--mcp-config") ?? throw new InvalidOperationException("no --mcp-config"));
}
