using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Server.Tests.Lifecycle;
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

    /// <summary>
    /// A recap is the session's standing (issue #466): kept with its time as soon as the server accepted its call, on the
    /// status and the list's summary, through the next turn's start and a turn that gives none, until another replaces it.
    /// </summary>
    [Fact]
    public async Task ASpeakWithARecap_KeepsItWithItsTime_UntilAnotherReplacesIt()
    {
        const string recap = "Pull request 456 er åben, testene er grønne, venter på review.";
        const string next = "Pull request 456 er merget, arbejdet er færdigt.";
        var go = Path.Combine(Path.GetTempPath(), $"speak-go-{Guid.NewGuid():N}");
        var done = Path.Combine(Path.GetTempPath(), $"speak-done-{Guid.NewGuid():N}");
        var script = new FakeScript().EmitInit()
            .AwaitStdin().EmitUser("Start", echo: true).Speak(Spoken, recap: recap).EmitAssistant("Skal jeg merge?").EmitResult("asked")
            // The next turn starts, and is held: it gives no recap, then one that replaces the first
            .AwaitStdin().EmitUser("Ja", echo: true).AwaitFile(go).Speak("Merget.", "toolu_plain")
            .AwaitFile(done).Speak("Færdig.", "toolu_next", recap: next).EmitAssistant("Merget.").EmitResult("merged")
            .AwaitStdin();
        var (run, id) = await StartAsync(script);
        await using var _ = run;

        var asked = await run.Client.WaitForAsync(id, s => s.State == ProjectState.WaitingInput, run.Server);
        Assert.Equal(recap, asked.Recap);
        var at = Assert.NotNull(asked.RecapAt);
        Assert.Contains("and as the session's recap", Assert.Single((await run.WaitForLaunchAsync(id, l => l.Calls.Count > 0)).Calls));
        var listed = Assert.Single(await run.Client.Hub.InvokeAsync<ProjectSummary[]>(nameof(IProjectHub.ListProjects)), p => p.Id == id);
        Assert.Equal((recap, at), (listed.Recap, listed.RecapAt));

        // The next turn's start clears its spoken reply, not its recap
        await run.Client.Hub.InvokeAsync(nameof(IProjectHub.ReplyAndResume), id, "Ja");
        // The next turn: the last one's result, and no spoken reply (turn 1's own recap push, mid-turn, has no result)
        var running = await run.Client.WaitForAsync(id, s => s is { State: ProjectState.Running, SpokenSummary: null, LastResult: "asked" }, run.Server);
        Assert.Equal((recap, at), (running.Recap, running.RecapAt));

        // A speak with no recap leaves it, and one with a recap replaces it, mid-turn
        File.WriteAllText(go, "");
        await run.WaitForLaunchAsync(id, l => l.Calls.Count > 1);
        Assert.Equal((recap, at), ((await StatusAsync(run, id)).Recap, (await StatusAsync(run, id)).RecapAt));
        File.WriteAllText(done, "");
        var merged = await run.Client.WaitForAsync(id, s => s is { State: ProjectState.Idle, LastResult: "merged" }, run.Server);
        Assert.Equal(next, merged.Recap);
        Assert.True(merged.RecapAt > at);
        File.Delete(go);
        File.Delete(done);
    }

    /// <summary>A recap voice cannot say refuses the whole call, with why: neither its text nor its recap is kept.</summary>
    [Fact]
    public async Task ARefusedRecap_RefusesTheCall_AndNothingIsKept()
    {
        var script = new FakeScript().EmitInit()
            .AwaitStdin().EmitUser("Start", echo: true).Speak(Spoken, recap: "PR #456 er åben.", refused: true)
            .EmitAssistant("Done.").EmitResult("done").AwaitStdin();
        var (run, id) = await StartAsync(script);
        await using var _ = run;

        var done = await run.Client.WaitForAsync(id, s => s.State == ProjectState.Idle, run.Server);
        Assert.Null(done.SpokenSummary);
        Assert.Null(done.Recap);
        Assert.Null(done.RecapAt);
        var call = Assert.Single((await run.WaitForLaunchAsync(id, l => l.Calls.Count > 0)).Calls);
        Assert.StartsWith($"{SpeakTool.Name} error:", call);
        Assert.Contains("The recap has '#'", call);
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

    /// <summary>
    /// A launch starts with no spoken reply (issue #411): one accepted in a turn the process died in, which no result
    /// ended, is not the next launch's, even for a turn that starts with no message the user sent.
    /// </summary>
    [Fact]
    public async Task ATextSpokenBeforeACrash_IsNotTheNextLaunchs()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit()
            .AwaitStdin().EmitUser("Start", echo: true).Speak("Før nedbruddet.", call: false).Exit(1));
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Error);

        harness.UseScript(new FakeScript().EmitInit().EmitAssistant("Done.").EmitResult("done").AwaitStdin());
        await harness.Projects.ResumeProjectAsync(created.Id);
        var done = await harness.WaitForStatusPushAsync(created.Id, s => s is { State: ProjectState.Idle, LastResult: "done" });
        Assert.Null(done.SpokenSummary);
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
    [InlineData("1. maj er releasen klar.", "1. maj er releasen klar.", null)]
    [InlineData("2. gang virkede det.", "2. gang virkede det.", null)]
    [InlineData("To ting:\n1. den ene\n2. den anden", "To ting: 1. den ene 2. den anden", "list")]
    public void Check_SaysTheTextAsVoiceSaysIt_OrWhyNot(string text, string spoken, string? refusedFor)
    {
        var (said, refused) = SpeakTool.Check(text);
        Assert.Equal(spoken, said);
        if (refusedFor is null) Assert.Null(refused);
        else Assert.Contains(refusedFor, refused);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("  \n ", null, null)]
    [InlineData("Pull request 456 er åben,\n venter på review.","Pull request 456 er åben, venter på review.", null)]
    [InlineData("Se https://github.com/x/y.", "Se https://github.com/x/y.", "The recap has a URL")]
    [InlineData("PR #456 er åben.", "PR #456 er åben.", "The recap has '#'")]
    public void CheckRecap_SaysTheRecapAsVoiceSaysIt_NoneForBlank_OrWhyNot(string? recap, string? said, string? refusedFor)
    {
        var (kept, refused) = SpeakTool.CheckRecap(recap);
        Assert.Equal(said, kept);
        if (refusedFor is null) Assert.Null(refused);
        else Assert.StartsWith(refusedFor, refused);
    }

    [Fact]
    public void CheckRecap_RefusesMoreThanALine_SayingHowLong()
    {
        var (_, refused) = SpeakTool.CheckRecap(new string('a', SpeakTool.RecapMaxLength + 1));
        Assert.StartsWith($"The recap is {SpeakTool.RecapMaxLength + 1} characters, and at most {SpeakTool.RecapMaxLength}", refused);
        Assert.Null(SpeakTool.CheckRecap(new string('a', SpeakTool.RecapMaxLength)).Refused);
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
    /// call to the server (with <paramref name="call"/>; none for a server with no MCP endpoint), then its tool result,
    /// an error when <paramref name="refused"/>, as claude writes it for a call the server refused. With its
    /// <paramref name="recap"/> and <paramref name="outcome"/>, when given.
    /// </summary>
    public static FakeScript Speak(this FakeScript script, string text, string toolUseId = "toolu_speak", string? parentToolUseId = null,
        bool refused = false, bool call = true, string? recap = null, string? outcome = null)
    {
        var input = new Dictionary<string, string> { ["text"] = text };
        if (recap is not null) input["recap"] = recap;
        if (outcome is not null) input["outcome"] = outcome;
        script.Emit(JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new { role = "assistant", content = new object[] { new { type = "tool_use", id = toolUseId, name = SpokenReply.ToolName, input } } },
            parent_tool_use_id = parentToolUseId,
            session_id = FakeScript.SessionIdPlaceholder,
        }));
        if (call)
            script.CallTool("godmode", SpeakTool.Name, input);
        return script.Emit(JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = toolUseId, content = refused ? "refused" : "kept", is_error = refused } } },
            parent_tool_use_id = parentToolUseId,
            session_id = FakeScript.SessionIdPlaceholder,
        }));
    }
}
