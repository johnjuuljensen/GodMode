using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Server.Tests.Lifecycle;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests;

/// <summary>
/// A session's activity (issue #468): when its main conversation last wrote a line (<see cref="ProjectStatus.LastOutputAt"/>),
/// which the list carries with its last turn's end (<see cref="ProjectSummary.LastResultAt"/>). Each line sets it in
/// memory, as the output offset is, with no status write: a subagent's line does not, and status.json has it from the
/// last write, the turn's end.
/// </summary>
public class LastActivityTests
{
    [Fact]
    public async Task EachLineOfTheMainConversation_SetsTheLastOutput_ASubagentsDoesNot_AndTheListCarriesIt()
    {
        var subagent = Path.Combine(Path.GetTempPath(), $"activity-sub-{Guid.NewGuid():N}");
        var main = Path.Combine(Path.GetTempPath(), $"activity-main-{Guid.NewGuid():N}");
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit()
            .AwaitStdin().EmitUser("Start", echo: true).EmitAssistant("Arbejder.").EmitResult("done")
            .AwaitFile(subagent).Emit(SubagentLine("Fra en subagent."))
            .AwaitFile(main).EmitAssistant("Videre.")
            .AwaitStdin());
        try
        {
            var created = await harness.CreateProjectAsync();
            await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
            var turnEnd = harness.Tracked(created.Id).Status;
            Assert.NotNull(turnEnd.LastOutputAt);
            Assert.NotNull(turnEnd.LastResultAt);

            var listed = Assert.Single(await harness.Projects.ListProjectsAsync(), p => p.Id == created.Id);
            Assert.Equal((turnEnd.LastOutputAt, turnEnd.LastResultAt), (listed.LastOutputAt, listed.LastResultAt));
            // The turn's end wrote status.json, with the time of its last line
            Assert.Equal(turnEnd.LastOutputAt, harness.ReadStatusFile(created.Id).LastOutputAt);

            File.WriteAllText(subagent, "");
            var afterSubagent = await NextLineAsync(harness, created.Id, turnEnd.OutputOffset);
            Assert.Equal(turnEnd.LastOutputAt, afterSubagent.LastOutputAt);

            File.WriteAllText(main, "");
            var afterMain = await NextLineAsync(harness, created.Id, afterSubagent.OutputOffset);
            Assert.True(afterMain.LastOutputAt > turnEnd.LastOutputAt, $"{afterMain.LastOutputAt:O} is not after {turnEnd.LastOutputAt:O}");
            Assert.Equal(afterMain.LastOutputAt, Assert.Single(await harness.Projects.ListProjectsAsync(), p => p.Id == created.Id).LastOutputAt);
            // A line that changes nothing else writes no status: UpdatedAt and status.json stay as the turn's end left them
            Assert.Equal(turnEnd.UpdatedAt, afterMain.UpdatedAt);
            Assert.Equal(turnEnd.LastOutputAt, harness.ReadStatusFile(created.Id).LastOutputAt);
        }
        finally
        {
            File.Delete(subagent);
            File.Delete(main);
        }
    }

    /// <summary>The project's status once a line after <paramref name="offset"/> has been handled.</summary>
    private static async Task<ProjectStatus> NextLineAsync(LifecycleHarness harness, string id, long offset)
    {
        var until = DateTime.UtcNow + LifecycleHarness.DefaultTimeout;
        while (DateTime.UtcNow < until)
        {
            if (harness.Tracked(id).Status is { } status && status.OutputOffset > offset)
                return status;
            await Task.Delay(20);
        }
        throw new TimeoutException($"no line after offset {offset} was handled");
    }

    /// <summary>An assistant line of a subagent's, under the Task call it runs in.</summary>
    /// <summary>
    /// #507: a line whose <c>parent_tool_use_id</c> is null, or that has none, is the main conversation's without being
    /// parsed; only one that may name a tool use is parsed, and a key inside a string or a nested object decides nothing.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"assistant","parent_tool_use_id":null}""", true, true)]
    [InlineData("""{"type":"assistant","parent_tool_use_id" :  null ,"x":1}""", true, true)]
    [InlineData("""{"type":"assistant"}""", true, true)]
    [InlineData("""{"type":"assistant","parent_tool_use_id":"toolu_1"}""", false, false)]
    [InlineData("""{"type":"assistant","input":{"parent_tool_use_id":"x"},"parent_tool_use_id":null}""", false, true)]
    [InlineData("""{"type":"user","text":"say \"parent_tool_use_id\": 1","parent_tool_use_id":null}""", true, true)]
    public void Only_a_line_that_may_name_a_tool_use_is_parsed(string line, bool unparsed, bool main)
    {
        Assert.Equal(unparsed, StatusUpdater.NoParentToolUse(line));
        Assert.Equal(main, StatusUpdater.IsConversationLine(new OutputEvent(DateTime.UtcNow, OutputEventType.Assistant, ""), line));
    }

    private static string SubagentLine(string text) => JsonSerializer.Serialize(new
    {
        type = "assistant",
        message = new { role = "assistant", content = new object[] { new { type = "text", text } } },
        parent_tool_use_id = "toolu_task",
        session_id = FakeScript.SessionIdPlaceholder,
    });
}
