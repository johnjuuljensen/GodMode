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
        await voice.Events.SaidAsync("issue 283, voice, har et spørgsmål.");
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

    /// <summary>
    /// 17:03:52 in the issue's log: the tool sent nothing, and the model said "Sendt." anyway. A project waiting on a
    /// permission is one the tool sends nothing to (the refusal of a final heard two ways is gone, #376).
    /// </summary>
    [Fact]
    public async Task Sendt_after_a_refused_answer_is_not_said()
    {
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.ProjectParameter] = "283", [VoiceTools.TextParameter] = "Ja, push." })
            .Respond("Sendt.");
        var servers = new FakeServers();
        await using var voice = await OfflineVoice.StartAsync(servers, model,
            connect: _ => { servers.Set(ServerA, Permission("p/r/283", "283-voice", "git push")); return Task.CompletedTask; });
        await voice.Events.SaidAsync("Klar.");
        // Answered once the announcement has been spoken: what is heard while it is, is not taken (#371)
        await voice.Events.SaidAsync("issue 283, voice, skal have tilladelse: git push. Svar på skærmen.");
        await voice.ListeningAsync();

        voice.Transcriptions.SayAsRecognized("Svar 283 ja");
        await voice.Events.SaidAsync(NothingSent);

        Assert.Contains("Nothing was sent", Assert.Single(model.ToolResults));
        Assert.Empty(servers.Replies);
        Assert.DoesNotContain("Sendt.", voice.Events.Responses);
    }

    /// <summary>An answer that went out is said in the code's words, with no model round after the call (#526).</summary>
    [Fact]
    public async Task An_answer_sent_is_said_sent_to_its_project()
    {
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.TextParameter] = "Push." });
        var (servers, voice) = await AskedAsync(model);
        await using var _ = voice;

        voice.Transcriptions.SayAsRecognized("Svar at den skal pushe");
        await voice.Events.SaidAsync("Sendt til issue 283.");

        Assert.Equal((P283, "Push."), Assert.Single(servers.Replies));
        Assert.Equal(1, model.Calls);
    }

    /// <summary>A send in an earlier turn is no send in this one.</summary>
    [Fact]
    public async Task Sendt_after_a_send_in_an_earlier_turn_is_not_said()
    {
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.TextParameter] = "Push." })
            .Respond("Sendt.");
        var (servers, voice) = await AskedAsync(model);
        await using var _ = voice;

        voice.Transcriptions.SayAsRecognized("Svar at den skal pushe");
        await voice.Events.SaidAsync("Sendt til issue 283.");
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
        await voice.Events.SaidAsync("issue 283, voice, skal have tilladelse: Bash: git push. Svar på skærmen.");

        voice.Transcriptions.SayAsRecognized("Svar ja");
        await voice.Events.SaidAsync(Reply);

        Assert.Empty(servers.Replies);
    }

    /// <summary>
    /// A project's own text read out in a turn with no send is said as the model put it, "sendt" and all: through
    /// read_reply, whose result the model says (a short status is the code's, #456).
    /// </summary>
    [Fact]
    public async Task A_projects_text_that_says_sendt_is_said_as_written()
    {
        const string Reply = "283 skrev: PR'en er sendt til review, skal jeg merge?";
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ReadReply, new() { [VoiceTools.ProjectParameter] = "283" })
            .Respond(Reply);
        var (servers, voice) = await AskedAsync(model);
        await using var _ = voice;

        voice.Transcriptions.SayAsRecognized("Hvad svarede 283?");
        await voice.Events.SaidAsync(Reply);

        Assert.Empty(servers.Replies);
        Assert.DoesNotContain(NothingSent, voice.Events.Responses);
    }

    /// <summary>
    /// #411: a project's reply read word for word through read_reply, which starts "Sendt til review.", is the project's
    /// words, said in full: not taken for the model's claim of a send and replaced by "Intet sendt".
    /// </summary>
    [Fact]
    public async Task A_reply_read_aloud_that_starts_sendt_is_said_in_full()
    {
        const string Reply = "Sendt til review. Pull requesten har to ændringer, og testene er grønne.";
        var servers = new FakeServers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ReadReply, new() { [VoiceTools.ProjectParameter] = "283" })
            .Respond(Reply);
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: _ =>
        {
            servers.AddProject(ServerA, "p/r/283", "283-voice");
            servers.SetReplies(ServerA, "p/r/283", new AssistantReply(Reply, true));
            return Task.CompletedTask;
        });
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Hvad svarede 283?");
        await voice.Events.SaidAsync(Reply);

        Assert.Empty(servers.Replies);
        Assert.DoesNotContain(NothingSent, voice.Events.Responses);
    }

    /// <summary>A reply read out that says "sendt" mid-sentence does not make the model's own "Sendt." true.</summary>
    [Fact]
    public async Task Sendt_after_reading_a_reply_that_says_sendt_mid_sentence_is_not_said()
    {
        var servers = new FakeServers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ReadReply, new() { [VoiceTools.ProjectParameter] = "283" })
            .Respond("Sendt.");
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: _ =>
        {
            servers.AddProject(ServerA, "p/r/283", "283-voice");
            servers.SetReplies(ServerA, "p/r/283", new AssistantReply("PR'en er sendt. Skal jeg merge?", true));
            return Task.CompletedTask;
        });
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Hvad svarede 283?");
        await voice.Events.SaidAsync(NothingSent);
    }

    [Theory]
    [InlineData("Sendt til review. PR'en er klar.", "283 (x): Idle. Last reply: Sendt til review. PR'en er klar.", true)]
    [InlineData("sendt til  review.\nPR'en","Last reply: Sendt til review. PR'en er klar.", true)]
    [InlineData("Sendt til review. PR'en er klar, og testene er grønne, alle sammen.", "Reply 2: Sendt til review. PR'en er klar, og testene er grønne, alle sammen, og den er merged.", true)]
    [InlineData("Sendt.", "Last reply: PR'en er sendt. Skal jeg merge?", false)]
    [InlineData("Sendt.", "Last reply: Klar.", false)]
    [InlineData("Sendt til 283.", "Nothing needs the user.", false)]
    public void A_reply_repeats_a_text_read_out_from_the_start_of_one_of_its_sentences(string reply, string readOut, bool repeats) =>
        Assert.Equal(repeats, SentNode.Repeats(reply, readOut));

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
    [InlineData("Sendt. Ingen andre venter.", true)]
    [InlineData("Sendt til 283, den fortsætter.", true)]
    [InlineData("PR'en er sendt til review, skal jeg merge?", false)]
    [InlineData("283 spørger: PR'en er sendt til review, skal jeg merge?", false)]
    [InlineData("Sent the PR for review.", false)]
    [InlineData("283 er færdig: sent the PR for review.", false)]
    public void A_reply_claims_a_send_when_it_says_sent_and_not_that_nothing_was(string reply, bool claims) =>
        Assert.Equal(claims, SentNode.ClaimsSend(reply));
}
