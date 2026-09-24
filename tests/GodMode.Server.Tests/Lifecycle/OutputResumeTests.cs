using GodMode.FakeClaude;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A client that comes back gets only what it has not seen: it resubscribes from the offset it
/// reached, the server replays from there in batches, and live output carries on after the replay
/// with nothing lost or repeated.
/// </summary>
public class OutputResumeTests
{
    private const int K = 20;
    private const int M = 40;

    /// <summary>Non-ASCII on purpose: offsets are bytes of UTF-8, not characters.</summary>
    private static string Text(string prefix, int i) => $"{prefix}{i} æøå — ✓ 🚀";

    /// <summary>An assistant line with <paramref name="text"/> unescaped, as the real CLI writes non-ASCII.</summary>
    private static string Assistant(string text) =>
        $$$$$"""{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"{{{{{text}}}}}"}]}}""";

    [Fact]
    public async Task Resubscribe_FromTheLastOffset_ReceivesEveryLineOnce_InOrder()
    {
        var script = new FakeScript().EmitInit().AwaitStdin();
        for (var i = 0; i < K; i++) script.Emit(Assistant(Text("a", i)));
        script.AwaitStdin();
        // Slow enough that the second subscription's replay overlaps lines still being written
        for (var i = 0; i < M; i++) script.Sleep(5).Emit(Assistant(Text("b", i)));
        script.EmitResult().AwaitStdin();
        await using var harness = new LifecycleHarness(script);
        var created = await harness.CreateProjectAsync();

        var first = harness.Connect("c1");
        await first.SubscribeAsync(created.Id, 0);
        await WaitForLineAsync(harness, first, created.Id, Text("a", K - 1));
        var offset = LastOffset(first, created.Id);
        await first.DisconnectAsync();

        await harness.Projects.SendInputAsync(created.Id, "more");
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.ReadOutputFile(created.Id).Contains(Text("b", 0))), null,
            () => $"the second turn did not start.\n{harness.Describe(created.Id)}");
        var second = harness.Connect("c2");
        await second.SubscribeAsync(created.Id, offset);
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        await WaitForLineAsync(harness, second, created.Id, "\"type\":\"result\"");

        var received = Lines(first, created.Id).Concat(Lines(second, created.Id)).ToArray();
        var written = FileLines(harness, created.Id);
        var duplicates = received.GroupBy(l => l).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        var gaps = written.Except(received).ToArray();
        Assert.True(duplicates.Length == 0 && gaps.Length == 0,
            $"resubscribing from offset {offset}: {duplicates.Length} lines received twice, {gaps.Length} never received " +
            $"({received.Length} received, {written.Length} written).\n{harness.Describe(created.Id)}");
        Assert.Equal(written, received);
    }

    [Fact]
    public async Task Replay_OfA5000LineTranscript_IsFewerThan50HubMessages()
    {
        const int lines = 5000;
        var script = new FakeScript().EmitInit();
        for (var i = 0; i < lines - 2; i++) script.Emit(Assistant(Text("line", i)));
        script.EmitResult().AwaitStdin();
        await using var harness = new LifecycleHarness(script);
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(FileLines(harness, created.Id).Length == lines), null,
            () => $"output.jsonl does not have {lines} lines.\n{harness.Describe(created.Id)}");

        var connection = harness.Connect("c1");
        await connection.SubscribeAsync(created.Id, 0);

        var messages = connection.Received.Count(p => p.ProjectId == created.Id && p.Method != nameof(IProjectHubClient.StatusChanged));
        Assert.Equal(FileLines(harness, created.Id), Lines(connection, created.Id));
        Assert.True(messages < 50, $"replaying {lines} lines took {messages} hub messages");
    }

    [Fact]
    public async Task EveryLine_CarriesTheByteOffsetAfterIt_AndTheStatusKeepsTheLast()
    {
        var script = new FakeScript().EmitInit();
        for (var i = 0; i < K; i++) script.Emit(Assistant(Text("a", i)));
        script.EmitResult().AwaitStdin();
        await using var harness = new LifecycleHarness(script);
        var created = await harness.CreateProjectAsync();
        var connection = harness.Connect("c1");
        await connection.SubscribeAsync(created.Id, 0);
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        await WaitForLineAsync(harness, connection, created.Id, "\"type\":\"result\"");

        var bytes = File.ReadAllBytes(Path.Combine(harness.ProjectPath(created.Id), ".godmode", "output.jsonl"));
        var ends = bytes.Select((b, i) => (b, i)).Where(x => x.b == (byte)'\n').Select(x => (long)x.i + 1).ToArray();
        Assert.Equal(ends, Received(connection, created.Id).Select(l => l.Offset));
        Assert.Equal(bytes.LongLength, (await harness.Projects.GetStatusAsync(created.Id)).OutputOffset);
    }

    [Fact]
    public async Task Subscribe_ReplaysInBatches_ThenCompletes_BeforeAnyLiveLine()
    {
        var script = new FakeScript().EmitInit().AwaitStdin();
        for (var i = 0; i < K; i++) script.Emit(Assistant(Text("a", i)));
        script.AwaitStdin();
        for (var i = 0; i < M; i++) script.Emit(Assistant(Text("b", i)));
        script.AwaitStdin();
        await using var harness = new LifecycleHarness(script);
        var created = await harness.CreateProjectAsync();
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(FileLines(harness, created.Id).Length == K + 1), null,
            () => $"the first lines were not written.\n{harness.Describe(created.Id)}");

        var connection = harness.Connect("c1");
        await connection.SubscribeAsync(created.Id, 0);
        await harness.Projects.SendInputAsync(created.Id, "more");
        await WaitForLineAsync(harness, connection, created.Id, Text("b", M - 1));

        var pushes = connection.Received.Where(p => p.ProjectId == created.Id && p.Method != nameof(IProjectHubClient.StatusChanged)).ToArray();
        var complete = Array.FindIndex(pushes, p => p.Method == nameof(IProjectHubClient.OutputReplayComplete));
        Assert.All(pushes[..complete], p => Assert.Equal(nameof(IProjectHubClient.OutputBatch), p.Method));
        Assert.All(pushes[(complete + 1)..], p => Assert.Equal(nameof(IProjectHubClient.OutputReceived), p.Method));
        Assert.Equal(0, pushes[0].Offset);
        Assert.Equal(pushes[complete - 1].Lines![^1].Offset, pushes[complete].Offset);
        Assert.Equal(FileLines(harness, created.Id), Lines(connection, created.Id));
    }

    [Theory]
    [InlineData(-1, new[] { "three" })]
    [InlineData(-2, new[] { "two", "three" })]
    [InlineData(-5, new[] { "one", "two", "three" })]
    public async Task SubscribeFromMinusN_ReplaysTheLastNTurns(long fromOffset, string[] turns)
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit()
            .EmitAssistant("one").EmitResult().EmitAssistant("two").EmitResult().EmitAssistant("three").EmitResult().AwaitStdin());
        var created = await harness.CreateProjectAsync();
        await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(FileLines(harness, created.Id).Length == 7), null,
            () => $"the turns were not written.\n{harness.Describe(created.Id)}");

        var connection = harness.Connect("c1");
        await connection.SubscribeAsync(created.Id, fromOffset);

        var assistant = Lines(connection, created.Id).Where(l => l.Contains("\"assistant\"")).ToArray();
        Assert.Equal(turns.Length, assistant.Length);
        Assert.All(turns.Zip(assistant), t => Assert.Contains($"\"{t.First}\"", t.Second));
        Assert.Contains("\"result\"", Lines(connection, created.Id)[^1]);
        Assert.Equal(turns.Length == 3, Lines(connection, created.Id)[0].Contains("\"init\""));
    }

    // ── What a client sees ──

    /// <summary>The output lines the connection received for the project, replayed and live, in order.</summary>
    private static OutputLine[] Received(HarnessConnection connection, string projectId) =>
        connection.Received.Where(p => p.ProjectId == projectId)
            .SelectMany(p => p.Method switch
            {
                nameof(IProjectHubClient.OutputBatch) => p.Lines!,
                nameof(IProjectHubClient.OutputReceived) => [new OutputLine(p.Offset!.Value, p.RawJson!)],
                _ => [],
            }).ToArray();

    private static string[] Lines(HarnessConnection connection, string projectId) =>
        Received(connection, projectId).Select(l => l.RawJson).ToArray();

    /// <summary>The offset a client resubscribes from: its last line's.</summary>
    private static long LastOffset(HarnessConnection connection, string projectId) => Received(connection, projectId)[^1].Offset;

    private static Task WaitForLineAsync(LifecycleHarness harness, HarnessConnection connection, string projectId, string text) =>
        LifecycleHarness.WaitUntilAsync(() => Task.FromResult(Lines(connection, projectId).Any(l => l.Contains(text))), null,
            () => $"{connection.ConnectionId} did not receive a line with {text}; it has {Lines(connection, projectId).Length}.\n{harness.Describe(projectId)}");

    private static string[] FileLines(LifecycleHarness harness, string projectId) =>
        harness.ReadOutputFile(projectId).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
}