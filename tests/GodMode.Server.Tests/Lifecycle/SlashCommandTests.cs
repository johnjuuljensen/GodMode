using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// Slash commands from the app (#31): the ones GodMode passes reach claude, the rest of claude's never do, and what
/// claude writes for <c>/clear</c> and <c>/compact</c> is handled: a clear starts the output over, and neither raises
/// a finished turn. The lines are the ones claude 2.1.287 writes headless.
/// </summary>
public class SlashCommandTests
{
    private static readonly string[] ClaudeCommands = ["my-skill", "clear", "compact", "context", "model", "rename", "brand-new"];

    private static string Init() => JsonSerializer.Serialize(new
    {
        type = "system", subtype = "init", session_id = FakeScript.SessionIdPlaceholder,
        slash_commands = ClaudeCommands, skills = new[] { "my-skill" },
    });

    // {{session_id}} is FakeScript.SessionIdPlaceholder
    private const string Reset =
        """{"type":"conversation_reset","new_conversation_id":"0b6f3c1e-1d2a-4c7e-9f00-5a6b7c8d9e0f","trigger":"clear","session_id":"{{session_id}}"}""";

    private const string CompactBoundary =
        """{"type":"system","subtype":"compact_boundary","session_id":"{{session_id}}","compact_metadata":{"trigger":"manual","pre_tokens":28063,"post_tokens":1916}}""";

    /// <summary>The result claude writes after /clear or /compact: no text, and the model took no turn.</summary>
    private const string SilentResult =
        """{"type":"result","subtype":"success","is_error":false,"num_turns":0,"result":"","session_id":"{{session_id}}","usage":{"input_tokens":0,"output_tokens":0}}""";

    /// <summary>A first turn that answers "hello", then a command's turn made of <paramref name="command"/>, then idle.</summary>
    private static FakeScript Script(params string[] command)
    {
        var script = new FakeScript().Emit(Init()).AwaitStdin().EmitAssistant("hello").Sleep(50).EmitResult("hello").AwaitStdin();
        foreach (var line in command) script.Emit(line);
        return script.AwaitStdin();
    }

    /// <summary>output.jsonl, or nothing while a clear has moved it away and not made the new one yet.</summary>
    private static string Output(LifecycleHarness harness, string projectId)
    {
        try { return harness.ReadOutputFile(projectId); }
        catch (FileNotFoundException) { return ""; }
    }

    [Fact]
    public async Task Init_ListsTheCommandsTheSessionPasses_AndClaudes()
    {
        await using var harness = new LifecycleHarness(Script());
        var created = await harness.CreateProjectAsync();
        var status = await harness.WaitForStateAsync(created.Id, ProjectState.Idle);

        Assert.Equal(["clear", "compact", "context", "recap", "my-skill"], status.SlashCommands);
        Assert.Equal(ClaudeCommands, status.ClaudeCommands);
        Assert.Equal(status.SlashCommands, harness.ReadStatusFile(created.Id).SlashCommands);
    }

    [Theory]
    [InlineData("/model opus")]
    [InlineData("/effort high")]
    [InlineData("/rename x")]
    [InlineData("/brand-new")] // only the session's claude lists it
    [InlineData("/config")]
    public async Task ACommandGodModeDoesNotPass_IsRefused_AndNeverReachesClaude(string input)
    {
        await using var harness = new LifecycleHarness(Script());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);

        var sent = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Projects.SendInputAsync(created.Id, input));
        var replied = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Projects.ReplyAndResumeAsync(created.Id, input));

        Assert.Contains(input.Split(' ')[0], sent.Message);
        Assert.Equal(sent.Message, replied.Message);
        Assert.Single(harness.Launches(created.Id)[0].Stdin);
        Assert.Equal(ProjectState.Idle, (await harness.Projects.GetStatusAsync(created.Id)).State);
    }

    [Theory]
    [InlineData("/my-skill do the thing")]
    [InlineData("/compact keep the plan")]
    [InlineData("/frobnicate the widget")] // claude knows no such command, and takes it as text
    [InlineData("/tmp/x is full")]
    public async Task ACommandGodModePasses_OrTextThatIsNone_ReachesClaude(string input)
    {
        await using var harness = new LifecycleHarness(Script());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);

        await harness.Projects.ReplyAndResumeAsync(created.Id, input);

        var launch = await harness.WaitForStdinAsync(created.Id, count: 2);
        Assert.Contains(JsonSerializer.Serialize(input)[1..^1], launch.Stdin[1]);
    }

    [Fact]
    public async Task ACreatePromptThatIsARefusedCommand_IsRefused_BeforeAnythingRuns()
    {
        await using var harness = new LifecycleHarness(Script());

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => harness.CreateProjectAsync(prompt: "/model opus"));

        Assert.Contains("/model", refused.Message);
        Assert.Empty(await harness.Projects.ListProjectsAsync());
    }

    [Fact]
    public async Task Clear_StartsTheOutputOver_KeepsTheOldBesideIt_AndRaisesNoFinished()
    {
        await using var harness = new LifecycleHarness(Script(Reset, Init(), SilentResult));
        var created = await harness.CreateProjectAsync();
        var live = harness.Connect("c1");
        await live.SubscribeAsync(created.Id, 0);
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        var statePath = harness.StatePath(created.Id);
        var before = await OutputLog.GenerationAsync(statePath);
        var offsetBefore = (await harness.Projects.GetStatusAsync(created.Id)).OutputOffset;

        await harness.Projects.SendInputAsync(created.Id, "/clear");
        await LifecycleHarness.WaitUntilAsync(
            async () => Output(harness, created.Id).Contains("\"num_turns\":0")
                && (await harness.Projects.GetStatusAsync(created.Id)).State == ProjectState.Idle,
            null, () => $"the clear did not end.\n{harness.Describe(created.Id)}");
        // A line is broadcast once its state is saved, after the state the wait saw (#371)
        await LifecycleHarness.WaitUntilAsync(
            () => Task.FromResult(live.Received.Any(p => p.ProjectId == created.Id && p.RawJson?.Contains("\"num_turns\":0") == true)),
            null, () => $"the clear's result did not reach the connection.\n{harness.Describe(created.Id)}");

        // The new generation's file starts with the reset; the old one is kept beside it, whole
        var after = await OutputLog.GenerationAsync(statePath);
        Assert.NotEqual(before, after);
        var lines = harness.ReadOutputFile(created.Id).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("conversation_reset", lines[0]);
        var kept = LifecycleHarness.ReadShared(OutputLog.ClearedPathOf(statePath, before));
        Assert.Contains("hello", kept);
        Assert.DoesNotContain("conversation_reset", kept);

        // A live connection hears the restart before the new generation's first line
        var pushes = live.Received.Where(p => p.ProjectId == created.Id && p.Method != nameof(IProjectHubClient.StatusChanged)).ToList();
        var restart = pushes.FindIndex(p => p.Method == nameof(IProjectHubClient.OutputRestarted));
        Assert.True(restart >= 0, "no OutputRestarted was pushed");
        Assert.Equal(after, pushes[restart].Generation);
        Assert.Contains("conversation_reset", pushes[restart + 1].RawJson);
        Assert.Equal(lines.Length, pushes.Count - restart - 1);

        // Nothing of the cleared conversation is left to show, and no turn finished
        var status = await harness.Projects.GetStatusAsync(created.Id);
        Assert.Null(status.LastResult);
        Assert.Null(status.LastResultAt);
        Assert.DoesNotContain(harness.Projects.GetAttention(), item => item.ProjectId == created.Id);
        Assert.Equal(new FileInfo(OutputLog.PathOf(statePath)).Length, status.OutputOffset);

        // A client that comes back with an offset from before the clear gets the new file, from 0
        var back = harness.Connect("c2");
        await back.SubscribeAsync(created.Id, offsetBefore, "s2", before);
        var batch = back.Received.First(p => p.Method == nameof(IProjectHubClient.OutputBatch));
        Assert.Equal((0L, after), (batch.Offset!.Value, batch.Generation));
        Assert.Contains("conversation_reset", batch.Lines![0].RawJson);
    }

    [Fact]
    public async Task Compact_KeepsTheOutputAndTheLastReply_AndRaisesNoFinished()
    {
        await using var harness = new LifecycleHarness(Script(CompactBoundary, SilentResult));
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        await harness.Projects.MarkSeenAsync(created.Id);
        var before = await OutputLog.GenerationAsync(harness.StatePath(created.Id));

        await harness.Projects.SendInputAsync(created.Id, "/compact");
        await LifecycleHarness.WaitUntilAsync(
            async () => Output(harness, created.Id).Contains("\"num_turns\":0")
                && (await harness.Projects.GetStatusAsync(created.Id)).State == ProjectState.Idle,
            null, () => $"the compact did not end.\n{harness.Describe(created.Id)}");

        var status = await harness.Projects.GetStatusAsync(created.Id);
        Assert.Equal("hello", status.LastResult);
        Assert.DoesNotContain(harness.Projects.GetAttention(), item => item.ProjectId == created.Id);
        Assert.Equal(before, await OutputLog.GenerationAsync(harness.StatePath(created.Id)));
        Assert.Contains("hello", harness.ReadOutputFile(created.Id));
    }
}
