using GodMode.Shared.Models;
using VoiceBot.Core.Speech;
using VoiceBot.Core.Tools;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// A final heard more than one way (VoiceBot#61) goes to the model with its earlier readings, and the model acts on
/// what it took the user to mean (issue #376): the tools that act do so on a final with readings as on any other. The
/// model asks only when the text it would send has two meanings, which the prompt says, and no tool refuses on a reading.
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

    /// <summary>A final as the recognizer sends it, with the earlier readings it revised.</summary>
    private static TranscriptionEvent Heard(string final, params string[] readings) =>
        new() { Text = final, IsPartial = false, Readings = readings };

    /// <summary>
    /// The four answers the guard refused in the voice log of 2026-10-01 (17:00:36): every earlier reading an ordinary
    /// revision, and none a meaning the answer could have had instead.
    /// </summary>
    public static TheoryData<string, string[], string> OrdinaryRevisions => new()
    {
        // 17:02:16: an earlier take on the same sentence
        { "Sige, at den skal tjekke loggen for UI applikationen, der kører nu.", ["Sige det, den skal tjekkes.", "Sige det, den skal tjekkes, der kører deroppe."],
            "Tjek loggen for UI-applikationen, der kører nu." },
        // 17:02:30: a later partial than the final went on from
        { "For den, der kører nu.", ["Den kører nu."], "Tjek loggen for den UI-applikation, der kører nu." },
        // 17:03:50: the same words, in the other language
        { "UI applikationens log.", ["UI application."], "Tjek UI-applikationens log." },
        // 17:07:58: one word, before a long final
        { "Så master undersøger den samtale, vi havde i går, og ser om den stadig er relevant for det, du arbejder på nu, og skriver hvad der mangler, før du går videre med at tjekke loggen for UI-applikationen.",
            ["Så må"], "Undersøg samtalen fra i går, se om den stadig er relevant, og skriv hvad der mangler, før du tjekker loggen." },
    };

    [Theory]
    [MemberData(nameof(OrdinaryRevisions))]
    public async Task An_answer_heard_with_ordinary_earlier_readings_is_sent(string final, string[] readings, string answer)
    {
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.TextParameter] = answer })
            .Respond("Sendt.");
        var (servers, voice) = await AskedAsync(model);
        await using var _ = voice;

        voice.Transcriptions.Add(Heard(final, readings));
        await voice.Events.SaidAsync("Sendt til 283.");

        Assert.Contains(model.UserTexts, t => t.Contains(final) && t.Contains("Earlier readings") && readings.All(t.Contains));
        Assert.StartsWith("Sent to 283", Assert.Single(model.ToolResults));
        Assert.Equal((P283, answer), Assert.Single(servers.Replies));
    }

    [Fact]
    public async Task Mark_seen_heard_two_ways_marks_it()
    {
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.MarkSeen, new() { [VoiceTools.ProjectParameter] = "283" })
            .Respond("Læst.");
        var (servers, voice) = await AskedAsync(model);
        await using var _ = voice;

        voice.Transcriptions.Add(Heard("Læst 283.", "Læs 283."));
        await voice.Events.SaidAsync("Læst.");

        Assert.Equal(P283, Assert.Single(servers.Seen));
    }

    [Fact]
    public async Task Muting_heard_two_ways_mutes()
    {
        var model = new ScriptedChatClient()
            .CallTool(AnnouncementTools.Mute.Name)
            .Respond("Stille.");
        var (_, voice) = await AskedAsync(model);
        await using var __ = voice;

        voice.Transcriptions.Add(Heard("Stille.", "Stil lige"));
        await voice.Events.SaidAsync("Stille.");

        Assert.DoesNotContain("Nothing was done", Assert.Single(model.ToolResults));
    }

    /// <summary>What a tool read out on a final with readings is what the conversation is about, as on any final.</summary>
    [Fact]
    public async Task A_status_read_on_a_final_heard_two_ways_is_what_the_conversation_is_about()
    {
        var servers = new FakeServers();
        servers.AddProject(ServerA, "p/r/101-x", "101-x");
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ProjectStatus, new() { [VoiceTools.ProjectParameter] = "101" })
            .Respond("101 er idle.")
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.TextParameter] = "Push." })
            .Respond("Sendt.");
        await using var voice = await OfflineVoice.StartAsync(servers, model,
            connect: _ => { servers.Set(ServerA, Question("p/r/283", "283-voice", "Skal jeg pushe?")); return Task.CompletedTask; });
        await voice.Events.SaidAsync("283 har et spørgsmål.");

        voice.Transcriptions.Add(Heard("Status 101.", "Status 10"));
        await voice.Events.SaidAsync("101 er idle.");
        voice.Transcriptions.SayAsRecognized("Svar at den skal pushe");
        await voice.Events.SaidAsync("Sendt til 101.");

        Assert.Equal(new ProjectRef(ServerA, "p/r/101-x"), Assert.Single(servers.Replies).Project);
    }

    /// <summary>
    /// The prompt the model gets with readings: act on the final, ask only when the text to send has two meanings, and
    /// then in the user's own words, never a fragment the recognizer heard; and no word that the tools do nothing.
    /// </summary>
    [Fact]
    public async Task The_prompt_says_to_act_on_the_final_and_ask_only_on_two_meanings()
    {
        var model = new ScriptedChatClient().Respond("Uklar.");
        var (_, voice) = await AskedAsync(model);
        await using var _ = voice;

        voice.Transcriptions.Add(Heard("Svar nej.", "Svar ja."));
        await voice.Events.SaidAsync("Uklar.");

        var prompt = model.Requests.Last().First(m => m.Role == Microsoft.Extensions.AI.ChatRole.System).Text;
        Assert.Contains("act on the final", prompt);
        Assert.Contains("two meanings", prompt);
        Assert.DoesNotContain("do nothing then", prompt);
    }
}
