using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.ProjectFiles;
using GodMode.Server.Services;
using GodMode.Shared.Enums;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// Issue #460: an answer sent by voice (the hub's ReplyByVoice) reaches the session marked as transcribed speech, on
/// claude's stdin and in the input the server logs, whether claude runs or is resumed with it. A typed reply
/// (ReplyAndResume) is not marked.
/// </summary>
public class SpokenReplyTests
{
    private const string Reply = "Delete the old branch";
    private static readonly string Marked = $"{SpokenInput.Marker}\n{Reply}";

    /// <summary>A session that has ended its first turn, and waits for the next input.</summary>
    private static FakeScript Waiting() => new FakeScript().EmitInit().AwaitStdin().EmitResult("first").AwaitStdin();

    /// <summary>The text a user message on claude's stdin carries.</summary>
    private static string TextOf(string stdinLine) =>
        JsonDocument.Parse(stdinLine).RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString()!;

    /// <summary>What the server logged as the session's input (input.jsonl), oldest first.</summary>
    private static IReadOnlyList<string> LoggedInput(LifecycleHarness harness, string projectId) =>
        [.. File.ReadAllLines(Path.Combine(harness.StatePath(projectId), SessionState.InputFileName))
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("content").GetString()!)];

    private static async Task<string> RunningAsync(LifecycleHarness harness)
    {
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStatusPushAsync(created.Id, s => s.LastResult == "first" && s.State == ProjectState.Idle);
        return created.Id;
    }

    [Fact]
    public async Task ASpokenReply_ToARunningSession_IsMarked()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var id = await RunningAsync(harness);

        await harness.Projects.ReplyAndResumeAsync(id, Reply, spoken: true);

        var launch = await harness.WaitForStdinAsync(id, count: 2);
        Assert.Equal(Marked, TextOf(launch.Stdin[1]));
        Assert.Equal(Marked, LoggedInput(harness, id)[^1]);
    }

    [Fact]
    public async Task ATypedReply_IsNotMarked()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var id = await RunningAsync(harness);

        await harness.Projects.ReplyAndResumeAsync(id, Reply);

        var launch = await harness.WaitForStdinAsync(id, count: 2);
        Assert.Equal(Reply, TextOf(launch.Stdin[1]));
        Assert.DoesNotContain(LoggedInput(harness, id), input => input.Contains(SpokenInput.Marker));
    }

    [Fact]
    public async Task ASpokenReply_ThatResumesAStoppedSession_IsMarked()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var id = await RunningAsync(harness);
        await harness.Projects.StopProjectAsync(id);
        await harness.WaitForStateAsync(id, ProjectState.Stopped);

        await harness.Projects.ReplyAndResumeAsync(id, Reply, spoken: true);

        var resumed = await harness.WaitForStdinAsync(id, index: 1);
        Assert.Equal(Marked, TextOf(resumed.Stdin[0]));
        Assert.Equal(Marked, LoggedInput(harness, id)[^1]);
    }

    [Theory]
    [InlineData("/compact", "/compact")]
    [InlineData("/clear  ", "/clear  ")]
    [InlineData("/tmp/x is full", "[via voice, transcribed]\n/tmp/x is full")]
    [InlineData("Ja, slet den", "[via voice, transcribed]\nJa, slet den")]
    public void Mark_LeavesACommandAsItIs(string spoken, string sent) =>
        Assert.Equal(sent, SpokenInput.Mark(spoken));
}
