using GodMode.Shared.Models;
using Microsoft.Extensions.AI;

namespace GodMode.Voice.Tests;

/// <summary>
/// Issue #547: a long reading is said slower than an ack (<see cref="SpeechPace"/>); "gentag", "spol tilbage", "fra
/// starten" and "gentag afsnittet" say it again by code (<see cref="ReplayNode"/>); and "pause", "fortsæt",
/// "langsommere" and "hurtigere" steer a reading while it plays (VoiceBot's playback commands, johnjuuljensen/VoiceBot#96).
/// </summary>
public sealed class ReadingControlTests
{
    private const string ServerA = "server-a";
    private const string Master = "p/r/master";

    /// <summary>A reply of many sentences, far past one spoken part, but under the cap on what is read: three parts.</summary>
    private static readonly string Long = string.Concat(Enumerable.Range(1, 60).Select(i => $"Trin {i}: relayet startede og forbandt uden fejl. ")).Trim();

    // Sentences of 50 characters or more, so VoiceBot requests each on its own
    private const string First = "Første sætning handler om relayet, som startede uden fejl i nat.";
    private const string Second = "Anden sætning handler om testene, som alle kørte grønt bagefter.";
    private const string Third = "Tredje sætning handler om pull requesten, som nu venter på review.";
    private const string Fourth = "Fjerde sætning handler om loggen, som ikke viste nogen advarsler.";
    private const string Fifth = "Femte sætning handler om master, som bygger grønt med alle rettelser.";
    private const string Reading = $"{First} {Second} {Third} {Fourth} {Fifth}";

    /// <summary>What the synthesizer was asked for, each with its speed (null: the synthesizer's own), in order.</summary>
    private static IReadOnlyList<(string Text, double? Speed)> Synthesized(OfflineVoice voice)
    {
        lock (voice.Synthesizer.Speeds) return [.. voice.Synthesizer.Texts.Zip(voice.Synthesizer.Speeds)];
    }

    private static double SpeedOf(OfflineVoice voice, string sentence) =>
        Synthesized(voice).Last(s => s.Text.Contains(sentence, StringComparison.Ordinal)).Speed ?? 1.0;

    private static Task RequestedAsync(OfflineVoice voice, string sentence) => Eventually.UntilAsync(
        () => Synthesized(voice).Any(s => s.Text.Contains(sentence, StringComparison.Ordinal)),
        () => $"\"{sentence}\" to be synthesized; it was asked for: {string.Join(" | ", Synthesized(voice).Select(s => s.Text))}");

    /// <summary>The issue's first point: an ack stays at the synthesizer's speed, a long reply is said slower.</summary>
    [Fact]
    public async Task A_long_reply_is_said_slower_than_an_ack()
    {
        var model = new ScriptedChatClient().Respond("Intet venter.").Respond(Reading);
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Hvad venter?");
        await voice.Events.SaidAsync("Intet venter.");
        voice.Transcriptions.SayAsRecognized("Hvad skete der i nat?");
        await voice.Events.SaidAsync(Reading);
        await RequestedAsync(voice, Fifth);

        Assert.Equal(1.0, SpeedOf(voice, "Intet venter."));
        Assert.Equal(1.0, SpeedOf(voice, "Klar."));
        Assert.All([First, Second, Fifth], s => Assert.True(SpeedOf(voice, s) < 0.9, $"\"{s}\" was said at {SpeedOf(voice, s)}"));
    }

    /// <summary>The issue's test: "fra starten" after two <c>read_more</c> pages says page one again, by code, and "mere" goes on from page two.</summary>
    [Fact]
    public async Task From_the_start_after_two_pages_says_page_one_again()
    {
        var servers = new FakeServers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ReadReply, new() { [VoiceTools.ProjectParameter] = "master" })
            .Respond("Master, del 1.")
            .CallTool(VoiceTools.ReadMore)
            .Respond("Master, del 2.")
            .CallTool(VoiceTools.ReadMore)
            .Respond("Master, del 3.")
            .CallTool(VoiceTools.ReadMore)
            .Respond("Master, del 2 igen.");
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: _ =>
        {
            servers.AddProject(ServerA, Master, "master");
            servers.SetReplies(ServerA, Master, new AssistantReply(Long, true));
            return Task.CompletedTask;
        });
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Læs masters svar.");
        await voice.Events.SaidAsync("Master, del 1.");
        voice.Transcriptions.SayAsRecognized("Læs videre.");
        await voice.Events.SaidAsync("Master, del 2.");
        voice.Transcriptions.SayAsRecognized("Læs videre.");
        await voice.Events.SaidAsync("Master, del 3.");
        var calls = model.Calls;

        voice.Transcriptions.SayAsRecognized("Fra starten.");
        await Eventually.UntilAsync(() => voice.Events.Responses.Count(r => r == "Master, del 1.") == 2,
            () => $"page one said again; the bot said: {string.Join(" | ", voice.Events.Responses)}");
        Assert.Equal(calls, model.Calls);

        voice.Transcriptions.SayAsRecognized("Læs videre.");
        await voice.Events.SaidAsync("Master, del 2 igen.");
        // The last tool result the model was given is part 2, read a second time
        var last = model.Requests.Last().SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last();
        Assert.StartsWith("master's reply, part 2 of 3", last.Result?.ToString());
    }

    /// <summary>The issue's test: "gentag" after a cut says it again from the sentence that was cut, by code.</summary>
    [Fact]
    public async Task Repeat_after_a_cut_says_it_from_the_cut_sentence()
    {
        var model = new ScriptedChatClient().Respond($"{First} {Second} {Third}");
        // Each sentence plays 1.5 s: the third is asked for a second before the second ends
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), model, speech: TimeSpan.FromMilliseconds(1500));
        await voice.Events.SaidAsync("Klar.");
        await voice.ListeningAsync();

        voice.Transcriptions.SayAsRecognized("Hvad skete der i nat?");
        await RequestedAsync(voice, Third);
        voice.Transcriptions.SayAsRecognized("Gentag.");

        await voice.Events.SaidAsync($"{Second} {Third}");
        Assert.Equal(1, model.Calls);
    }

    /// <summary>The issue's test: "langsommere" while a reading plays lowers the speed of the sentences after it, without cutting it.</summary>
    [Fact]
    public async Task Slower_mid_reading_lowers_the_speed_of_the_next_sentences()
    {
        var model = new ScriptedChatClient().Respond(Reading);
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), model, speech: TimeSpan.FromMilliseconds(1500));
        await voice.Events.SaidAsync("Klar.");
        await voice.ListeningAsync();

        voice.Transcriptions.SayAsRecognized("Hvad skete der i nat?");
        await RequestedAsync(voice, Second);
        voice.Transcriptions.SayAsRecognized("Langsommere.");
        await RequestedAsync(voice, Fifth);

        Assert.True(SpeedOf(voice, Fourth) < SpeedOf(voice, First), $"{SpeedOf(voice, Fourth)} after, {SpeedOf(voice, First)} before");
        Assert.True(SpeedOf(voice, Fifth) < SpeedOf(voice, First));
        Assert.Equal(1, model.Calls);
    }
}
