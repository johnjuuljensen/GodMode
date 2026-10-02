using GodMode.Shared.Models;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// "Sendt" is the code's word, never the model's (issue #375): only an answer the tool sent in this turn is said to be
/// sent, in the code's words. A reply that claims a send when none went out this turn is not said: the user hears that
/// nothing was sent, and to say the answer again.
/// </summary>
public sealed class SentTests
{
    private const string ServerA = "server-a";
    private static readonly ProjectRef P283 = new(ServerA, "p/r/283");
    private const string NothingSent = "Intet sendt. Sig svaret igen.";

    /// <summary>283 asks "shall I push?", announced alone: an unnamed answer goes to it.</summary>
    private static async Task<(FakeServers Servers, OfflineVoice Voice)> AskedAsync(ScriptedChatClient model)
    {
        var servers = new FakeServers();
        var voice = await OfflineVoice.StartAsync(servers, model,
            connect: _ => { servers.Set(ServerA, Question("p/r/283", "283-voice", "Skal jeg pushe?")); return Task.CompletedTask; });
        await voice.Events.SaidAsync("283 har et spørgsmål.");
        return (servers, voice);
    }

    /// <summary>17:12:05 in the issue's log: "Det kan du skrive til dem." got "Sendt." with no tool call at all.</summary>
    [Fact]
    public async Task Sendt_with_no_answer_sent_is_not_said()
    {
        var (servers, voice) = await AskedAsync(new ScriptedChatClient().Respond("Sendt."));
        await using var _ = voice;

        voice.Transcriptions.SayAsRecognized("Det kan du skrive til dem");
        await voice.Events.SaidAsync(NothingSent);

        Assert.Empty(servers.Replies);
        Assert.DoesNotContain("Sendt.", voice.Events.Responses);
    }

    /// <summary>17:03:52 in the issue's log: the tool returned "Nothing was done", and the model said "Sendt." anyway.</summary>
    [Fact]
    public async Task Sendt_after_a_refused_answer_is_not_said()
    {
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.TextParameter] = "Ja, push." })
            .Respond("Sendt.");
        var (servers, voice) = await AskedAsync(model);
        await using var _ = voice;

        voice.Transcriptions.AddPartial("Svar ja");
        voice.Transcriptions.AddFinal("Svar nej");
        await voice.Events.SaidAsync(NothingSent);

        Assert.StartsWith("Nothing was done", Assert.Single(model.ToolResults));
        Assert.Empty(servers.Replies);
        Assert.DoesNotContain("Sendt.", voice.Events.Responses);
    }

    /// <summary>An answer that went out is said in the code's words, whatever the model replied.</summary>
    [Fact]
    public async Task An_answer_sent_is_said_sent_to_its_project()
    {
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.TextParameter] = "Push." })
            .Respond("Sendt.");
        var (servers, voice) = await AskedAsync(model);
        await using var _ = voice;

        voice.Transcriptions.SayAsRecognized("Svar at den skal pushe");
        await voice.Events.SaidAsync("Sendt til 283.");

        Assert.Equal((P283, "Push."), Assert.Single(servers.Replies));
        Assert.DoesNotContain("Sendt.", voice.Events.Responses);
    }

    /// <summary>A send in an earlier turn is no send in this one.</summary>
    [Fact]
    public async Task Sendt_after_a_send_in_an_earlier_turn_is_not_said()
    {
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.TextParameter] = "Push." })
            .Respond("Sendt.")
            .Respond("Sendt.");
        var (servers, voice) = await AskedAsync(model);
        await using var _ = voice;

        voice.Transcriptions.SayAsRecognized("Svar at den skal pushe");
        await voice.Events.SaidAsync("Sendt til 283.");
        voice.Transcriptions.SayAsRecognized("Det kan du skrive til dem");
        await voice.Events.SaidAsync(NothingSent);

        Assert.Single(servers.Replies);
    }

    /// <summary>A reply that says nothing was sent is true, and is said as the model put it.</summary>
    [Fact]
    public async Task A_reply_that_says_nothing_was_sent_is_said()
    {
        const string Reply = "283 skal have tilladelse: Bash: git push. Svar på skærmen. Intet sendt.";
        var servers = new FakeServers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.TextParameter] = "Ja." })
            .Respond(Reply);
        await using var voice = await OfflineVoice.StartAsync(servers, model,
            connect: _ => { servers.Set(ServerA, Permission("p/r/283", "283-voice", "Bash: git push")); return Task.CompletedTask; });
        await voice.Events.SaidAsync("283 skal have tilladelse: Bash: git push. Svar på skærmen.");

        voice.Transcriptions.SayAsRecognized("Svar ja");
        await voice.Events.SaidAsync(Reply);

        Assert.Empty(servers.Replies);
    }

    [Theory]
    [InlineData("Sendt.", true)]
    [InlineData("Sendt til 283.", true)]
    [InlineData("Det er sendt.", true)]
    [InlineData("Sent to 283.", true)]
    [InlineData("SENDT", true)]
    [InlineData("Intet sendt.", false)]
    [InlineData("Det er ikke sendt: svar på skærmen.", false)]
    [InlineData("Nothing was sent.", false)]
    [InlineData("Not sent.", false)]
    [InlineData("Mente du ja eller nej til 283?", false)]
    [InlineData("Udsendelsen kører.", false)]
    public void A_reply_claims_a_send_when_it_says_sent_and_not_that_nothing_was(string reply, bool claims) =>
        Assert.Equal(claims, SentNode.ClaimsSend(reply));
}
