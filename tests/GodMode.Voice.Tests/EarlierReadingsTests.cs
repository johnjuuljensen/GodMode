using GodMode.Shared.Models;
using VoiceBot.Core.Tools;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// A final heard more than one way (VoiceBot#61: "svar ja" revised into "svar nej") goes to the model with its earlier
/// readings, and the model is told to answer what the user most plausibly meant. Nothing is sent or changed on that
/// guess: the tools that act do nothing and say to ask, and act on the next final heard one way.
/// </summary>
public sealed class EarlierReadingsTests
{
    private const string ServerA = "server-a";
    private static readonly ProjectRef P283 = new(ServerA, "p/r/283");

    /// <summary>283 asks "shall I push?", announced alone: an unnamed answer goes to it.</summary>
    private static async Task<(FakeServers Servers, OfflineVoice Voice)> AskedAsync(ScriptedChatClient model)
    {
        var servers = new FakeServers();
        var voice = await OfflineVoice.StartAsync(servers, model,
            connect: _ => { servers.Set(ServerA, Question("p/r/283", "283-voice", "Skal jeg pushe?")); return Task.CompletedTask; });
        await voice.Events.SaidAsync("283 har et spørgsmål.");
        return (servers, voice);
    }

    [Fact]
    public async Task An_answer_heard_two_ways_is_not_sent_and_the_next_answer_heard_one_way_is()
    {
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.TextParameter] = "Ja, push." })
            .Respond("Mente du ja eller nej til 283?")
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.TextParameter] = "Nej, push ikke." })
            .Respond("Sendt til 283.");
        var (servers, voice) = await AskedAsync(model);
        await using var _ = voice;

        voice.Transcriptions.AddPartial("Svar ja");
        voice.Transcriptions.AddFinal("Svar nej");
        await voice.Events.SaidAsync("Mente du ja eller nej til 283?");

        Assert.Empty(servers.Replies);
        Assert.StartsWith("Nothing was done: the user was heard more than one way, \"Svar nej\" and earlier \"Svar ja\".",
            Assert.Single(model.ToolResults));

        voice.Transcriptions.SayAsRecognized("Nej");
        await voice.Events.SaidAsync("Sendt til 283.");

        Assert.Equal((P283, "Nej, push ikke."), Assert.Single(servers.Replies));
    }

    [Fact]
    public async Task Mark_seen_heard_two_ways_does_nothing_and_heard_one_way_marks_it()
    {
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.MarkSeen, new() { [VoiceTools.ProjectParameter] = "283" })
            .Respond("Mente du læst eller læs 283?")
            .CallTool(VoiceTools.MarkSeen, new() { [VoiceTools.ProjectParameter] = "283" })
            .Respond("Læst.");
        var (servers, voice) = await AskedAsync(model);
        await using var _ = voice;

        voice.Transcriptions.AddPartial("Læst 283");
        voice.Transcriptions.AddFinal("Læs 283");
        await voice.Events.SaidAsync("Mente du læst eller læs 283?");
        Assert.Empty(servers.Seen);

        voice.Transcriptions.SayAsRecognized("Læst 283");
        await voice.Events.SaidAsync("Læst.");

        Assert.Equal(P283, Assert.Single(servers.Seen));
    }

    [Fact]
    public async Task Muting_heard_two_ways_does_nothing()
    {
        var model = new ScriptedChatClient()
            .CallTool(AnnouncementTools.Mute.Name)
            .Respond("Mente du stille?");
        var (_, voice) = await AskedAsync(model);
        await using var __ = voice;

        voice.Transcriptions.AddPartial("Stille");
        voice.Transcriptions.AddFinal("Stil lige om");
        await voice.Events.SaidAsync("Mente du stille?");

        Assert.StartsWith("Nothing was done", Assert.Single(model.ToolResults));
    }

    /// <summary>A tool that reads may run on a guess, but what it read out is not what the conversation is about.</summary>
    [Fact]
    public async Task A_status_read_on_a_final_heard_two_ways_leaves_the_conversation_where_it_was()
    {
        var servers = new FakeServers();
        servers.AddProject(ServerA, "p/r/101-x", "101-x");
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ProjectStatus, new() { [VoiceTools.ProjectParameter] = "101" })
            .Respond("101 er idle.")
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.TextParameter] = "Push." })
            .Respond("Sendt til 283.");
        await using var voice = await OfflineVoice.StartAsync(servers, model,
            connect: _ => { servers.Set(ServerA, Question("p/r/283", "283-voice", "Skal jeg pushe?")); return Task.CompletedTask; });
        await voice.Events.SaidAsync("283 har et spørgsmål.");

        voice.Transcriptions.AddPartial("Status 101");
        voice.Transcriptions.AddFinal("Status 283");
        await voice.Events.SaidAsync("101 er idle.");
        voice.Transcriptions.SayAsRecognized("Svar at den skal pushe");
        await voice.Events.SaidAsync("Sendt til 283.");

        Assert.Equal(P283, Assert.Single(servers.Replies).Project);
    }

    /// <summary>The tools on their own: nothing acts while the conversation is unsure, and all act again after.</summary>
    [Fact]
    public async Task The_tools_that_act_refuse_while_unsure_and_act_after()
    {
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var conversation = new VoiceConversation();
        var tools = new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, conversation);
        servers.Set(ServerA, Question("p/r/283", "283-voice", "Skal jeg pushe?"));

        conversation.Unsure = new HeardTwoWays("Svar nej", ["Svar ja"]);
        Assert.StartsWith("Nothing was done", await tools.AnswerAsync("283", "Ja.", CancellationToken.None));
        Assert.StartsWith("Nothing was done", await tools.MarkSeenAsync("283", CancellationToken.None));
        Assert.Empty(servers.Replies);
        Assert.Empty(servers.Seen);

        conversation.Unsure = null;
        Assert.StartsWith("Sent to 283", await tools.AnswerAsync("283", "Nej.", CancellationToken.None));
        Assert.Single(servers.Replies);
    }
}
