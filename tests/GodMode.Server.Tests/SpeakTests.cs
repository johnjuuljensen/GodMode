using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using static GodMode.Server.Tests.FleetRun;

namespace GodMode.Server.Tests;

/// <summary>
/// A session's own spoken reply (issue #384), against the real server with FakeClaude: <c>speak</c> on GodMode's MCP
/// endpoint checks the text, and the text the server accepted in a turn's main conversation is the status's
/// <see cref="ProjectStatus.SpokenSummary"/> and the attention item's <see cref="AttentionItem.Spoken"/> once the turn
/// ends, until the next turn starts. The fake writes the tool's lines around its real call, as claude writes them.
/// </summary>
public class SpeakTests
{
    private const string Spoken = "Rettelsen er pushet og testene er grønne. Skal jeg åbne pull requesten?";

    /// <summary>A session in the work action, whose first launch plays <paramref name="script"/>.</summary>
    private static async Task<(FleetRun Run, string Id)> StartAsync(FakeScript script)
    {
        var run = await FleetRun.StartAsync(script);
        return (run, await run.CreateOverHubAsync("talker"));
    }

    private static async Task<ProjectStatus> StatusAsync(FleetRun run, string id) =>
        await run.Client.Hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.GetStatus), id);

    private static async Task<AttentionItem?> ItemAsync(FleetRun run, string id) =>
        (await run.Client.Hub.InvokeAsync<AttentionItem[]>(nameof(IProjectHub.GetAttention))).SingleOrDefault(i => i.ProjectId == id);

    [Fact]
    public async Task ASessionThatSpeaks_CarriesTheTextWithItsTurn_AndTheNextTurnClearsIt()
    {
        var go = Path.Combine(Path.GetTempPath(), $"speak-go-{Guid.NewGuid():N}");
        var script = new FakeScript().EmitInit()
            .AwaitStdin().EmitUser("Start", echo: true).Speak(Spoken)
            .EmitAssistant("## Status\n\n| test | result |\n|---|---|\n| all | green |\n\nSkal jeg åbne pull requesten?").EmitResult("the long reply")
            // The next turn starts, and is held until the test lets it go, with no speak in it
            .AwaitStdin().EmitUser("Ja", echo: true).AwaitFile(go).EmitAssistant("Åbnet.").EmitResult("opened")
            .AwaitStdin();
        var (run, id) = await StartAsync(script);
        await using var _ = run;

        var asked = await run.Client.WaitForAsync(id, s => s.State == ProjectState.WaitingInput, run.Server);
        Assert.Equal(Spoken, asked.SpokenSummary);
        Assert.Equal("the long reply", asked.LastResult);
        var item = await ItemAsync(run, id);
        Assert.Equal(AttentionKind.Question, item?.Kind);
        Assert.Equal(Spoken, item?.Spoken);
        Assert.StartsWith($"{SpeakTool.Name} Kept as this turn's spoken reply", Assert.Single((await run.WaitForLaunchAsync(id, l => l.Calls.Count > 0)).Calls));

        // The user's answer starts the next turn (claude echoes it), which is held: what was said for the last one is
        // gone while it runs, before it has a result
        await run.Client.Hub.InvokeAsync(nameof(IProjectHub.ReplyAndResume), id, "Ja");
        await run.Client.WaitForAsync(id, s => s is { State: ProjectState.Running, SpokenSummary: null }, run.Server);
        Assert.Null(await ItemAsync(run, id));

        File.WriteAllText(go, "");
        var done = await run.Client.WaitForAsync(id, s => s is { State: ProjectState.Idle, LastResult: "opened" }, run.Server);
        Assert.Null(done.SpokenSummary);
        var finished = await ItemAsync(run, id);
        Assert.Equal(AttentionKind.Finished, finished?.Kind);
        Assert.Null(finished?.Spoken);
        File.Delete(go);
    }

    /// <summary>A text voice cannot say is refused with why, and the turn has no spoken reply: voice falls back to the result.</summary>
    [Fact]
    public async Task ARefusedText_IsRefusedWithWhy_AndTheTurnHasNoSpokenReply()
    {
        var script = new FakeScript().EmitInit()
            .AwaitStdin().EmitUser("Start", echo: true).Speak(new string('a', SpeakTool.MaxLength + 1), refused: true)
            .EmitAssistant("Done.").EmitResult("done").AwaitStdin();
        var (run, id) = await StartAsync(script);
        await using var _ = run;

        var done = await run.Client.WaitForAsync(id, s => s.State == ProjectState.Idle, run.Server);
        Assert.Null(done.SpokenSummary);
        var call = Assert.Single((await run.WaitForLaunchAsync(id, l => l.Calls.Count > 0)).Calls);
        Assert.StartsWith($"{SpeakTool.Name} error:", call);
        Assert.Contains($"at most {SpeakTool.MaxLength}", call);
        Assert.Null((await ItemAsync(run, id))?.Spoken);
    }

    /// <summary>
    /// A subagent's speak is not the session's reply (its lines name the tool use it runs under), and of the main
    /// conversation's calls the last the server accepted is the turn's.
    /// </summary>
    [Fact]
    public async Task ASubagentsSpeak_IsNotTheSessions_AndTheLastAcceptedCallIsTheTurns()
    {
        var script = new FakeScript().EmitInit()
            .AwaitStdin().EmitUser("Start", echo: true)
            .Speak("Først dette.", "toolu_first")
            .Speak("Fra en subagent.", "toolu_sub", parentToolUseId: "toolu_task")
            .Speak("Til sidst dette.", "toolu_last")
            .Speak("**Fed** tekst.", "toolu_refused", refused: true)
            .EmitAssistant("Done.").EmitResult("done").AwaitStdin();
        var (run, id) = await StartAsync(script);
        await using var _ = run;

        var done = await run.Client.WaitForAsync(id, s => s.State == ProjectState.Idle, run.Server);
        Assert.Equal("Til sidst dette.", done.SpokenSummary);
        Assert.Equal("Til sidst dette.", (await ItemAsync(run, id))?.Spoken);
    }

    /// <summary>A turn that ends in error says nothing it spoke: the error is what needs the user.</summary>
    [Fact]
    public async Task ATurnThatFails_HasNoSpokenReply()
    {
        var script = new FakeScript().EmitInit()
            .AwaitStdin().EmitUser("Start", echo: true).Speak(Spoken)
            .EmitResult("it broke", isError: true).AwaitStdin();
        var (run, id) = await StartAsync(script);
        await using var _ = run;

        var failed = await run.Client.WaitForAsync(id, s => s.State == ProjectState.Error, run.Server);
        Assert.Null(failed.SpokenSummary);
        Assert.Null((await ItemAsync(run, id))?.Spoken);
    }

    /// <summary>The server asks every session to speak in its instructions on the session endpoint, and the fleet's says nothing of it.</summary>
    [Fact]
    public async Task TheSessionEndpointsInstructions_AskForSpeak_AndTheFleetEndpointsDoNot()
    {
        var (run, id) = await StartAsync(new FakeScript().EmitInit().AwaitStdin());
        await using var _ = run;
        var entry = GodModeMcpEntry.Of(await run.WaitForLaunchAsync(id, l => l.Stdin.Count > 0));

        await using var session = await ConnectAsync(entry);
        Assert.Equal(SpeakTool.Instructions, session.ServerInstructions);
        await using var fleet = await run.ConnectFleetAsync();
        Assert.Null(fleet.ServerInstructions);
    }

    [Theory]
    [InlineData("Det virker.  Skal jeg\n pushe?", "Det virker. Skal jeg pushe?", null)]
    [InlineData("", "", "empty")]
    [InlineData("Se **dette**.", "Se **dette**.", "markdown")]
    [InlineData("Ret `Foo.cs`.", "Ret `Foo.cs`.", "markdown")]
    [InlineData("PR #413 er klar.", "PR #413 er klar.", "\"pull request 413\"")]
    [InlineData("Se https://github.com/x/y.", "Se https://github.com/x/y.", "URL")]
    [InlineData("To ting:\n- den ene\n- den anden", "To ting: - den ene - den anden", "list")]
    [InlineData("Ja - det virker, 2. gang.", "Ja - det virker, 2. gang.", null)]
    public void Check_SaysTheTextAsVoiceSaysIt_OrWhyNot(string text, string spoken, string? refusedFor)
    {
        var (said, refused) = SpeakTool.Check(text);
        Assert.Equal(spoken, said);
        if (refusedFor is null) Assert.Null(refused);
        else Assert.Contains(refusedFor, refused);
    }

    [Fact]
    public void Check_RefusesALongText_SayingHowLong()
    {
        var (_, refused) = SpeakTool.Check(new string('a', SpeakTool.MaxLength + 5));
        Assert.Contains($"{SpeakTool.MaxLength + 5} characters", refused);
        Assert.Null(SpeakTool.Check(new string('a', SpeakTool.MaxLength)).Refused);
    }
}

/// <summary>The lines claude writes around a <c>speak</c> call, with the call itself between them.</summary>
internal static class SpeakScript
{
    /// <summary>
    /// The model's <c>speak</c> tool use (under <paramref name="parentToolUseId"/>, a subagent's, when given), the real
    /// call to the server, then its tool result, an error when <paramref name="refused"/>, as claude writes it for a call
    /// the server refused.
    /// </summary>
    public static FakeScript Speak(this FakeScript script, string text, string toolUseId = "toolu_speak", string? parentToolUseId = null, bool refused = false) =>
        script
            .Emit(JsonSerializer.Serialize(new
            {
                type = "assistant",
                message = new { role = "assistant", content = new object[] { new { type = "tool_use", id = toolUseId, name = SpokenReply.ToolName, input = new { text } } } },
                parent_tool_use_id = parentToolUseId,
                session_id = FakeScript.SessionIdPlaceholder,
            }))
            .CallTool("godmode", SpeakTool.Name, new { text })
            .Emit(JsonSerializer.Serialize(new
            {
                type = "user",
                message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = toolUseId, content = refused ? "refused" : "kept", is_error = refused } } },
                parent_tool_use_id = parentToolUseId,
                session_id = FakeScript.SessionIdPlaceholder,
            }));
}
