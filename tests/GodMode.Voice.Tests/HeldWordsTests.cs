using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// What the model waited past is joined to what comes next (#529): as in the issue's log (14:31:25), "FE-86, say,
/// recently we had a case called FE-86. It's a case about…" ended on a pause, the model waited, and the next final,
/// "some statistics…", was sent alone.
/// </summary>
public sealed class HeldWordsTests
{
    private const string Start = "FE-86, recently we had a case called FE-86. It's a case about";
    private const string Rest = "some statistics. It was written directly by a stakeholder.";

    [Fact]
    public async Task A_final_the_model_waited_past_goes_on_into_the_next()
    {
        var model = new ScriptedChatClient()
            .Answer(new() { ["action"] = "wait", ["response_text"] = "", ["note"] = "mid-sentence" })
            .Respond("Okay.");
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized(Start);
        await Eventually.UntilAsync(() => model.Calls == 1, () => $"the model to be asked once; it was asked {model.Calls} times");
        voice.Transcriptions.SayAsRecognized(Rest);
        await voice.Events.SaidAsync("Okay.");

        Assert.Equal(2, model.Calls);
        Assert.Contains($"{Start} {Rest}", model.UserTexts[^1]);
    }

    [Fact]
    public void What_was_held_is_taken_once_and_only_for_a_while()
    {
        var time = new ManualTime();
        var conversation = new VoiceConversation(time);

        conversation.Hold(Start);
        Assert.Equal(Start, conversation.TakeHeld());
        Assert.Null(conversation.TakeHeld());

        conversation.Hold(Start);
        time.Advance(VoiceConversation.HeldFor);
        Assert.Null(conversation.TakeHeld());
    }

    /// <summary>
    /// A request that names no project, when none is current, offers the one the user talked about last first (#529):
    /// "Process the rest of the log" was offered only the two waiting sessions, not the chat the talk was about.
    /// </summary>
    [Fact]
    public async Task An_unaddressed_request_offers_the_project_talked_about_last_first()
    {
        var servers = new FakeServers("a");
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var conversation = new VoiceConversation();
        var tools = new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, conversation);
        servers.AddProject("a", "Mega/Mega-Assistant/general", "general", root: "Mega-Assistant", kind: "chat", profile: "Mega");
        servers.Set("a", Question("Mega/Mega-Assistant/fe86", "FE86", "Which statistics?"));

        await tools.ProjectStatusAsync("general", CancellationToken.None);
        // Two announced at once since: nothing is current
        conversation.Announced(null);

        var result = await tools.AnswerAsync(null, "Process the rest of the log.", CancellationToken.None);

        Assert.Contains("No project is being talked about: ask the user which one. The user last talked about chat general", result);
        Assert.Empty(servers.Replies);
    }
}
