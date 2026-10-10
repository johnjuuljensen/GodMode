using GodMode.Shared.Models;
using VoiceBot.Core.Pipeline;
using VoiceBot.Core.Resources;
using VoiceBot.Core.Speech;

namespace GodMode.Voice.Tests;

/// <summary>
/// Issue #547's code side, without a session: the pace of a line by its words (<see cref="SpeechPace"/>), and what
/// <see cref="ReplayNode"/> says again from what was said last and where it was cut (VoiceBot's <c>LastSpeech</c>).
/// </summary>
public sealed class ReplayTests
{
    private const string First = "Første sætning handler om relayet, som startede uden fejl i nat.";
    private const string Second = "Anden sætning handler om testene, som alle kørte grønt bagefter.";
    private const string Third = "Tredje sætning handler om pull requesten, som nu venter på review.";
    private const string Line = $"{First} {Second} {Third}";
    private static readonly SessionLanguages Languages = SessionLanguages.Parse("da-DK+en");

    private static string Words(int count) => string.Join(' ', Enumerable.Repeat("ord", count));

    [Fact]
    public void A_short_line_is_said_at_the_synthesizers_speed_and_a_long_one_eases_down_to_the_slow_speed()
    {
        Assert.Null(SpeechPace.Of("Klar."));
        Assert.Null(SpeechPace.Of(Words(SpeechPace.FastUpToWords)));
        var slowest = SpeechPace.SlowSpeed / SpeechPace.FastSpeed;
        Assert.Equal(slowest, SpeechPace.Of(Words(SpeechPace.SlowFromWords))!.Value, 6);
        Assert.Equal(slowest, SpeechPace.Of(Words(200))!.Value, 6);
        var between = SpeechPace.Of(Words((SpeechPace.FastUpToWords + SpeechPace.SlowFromWords) / 2))!.Value;
        Assert.InRange(between, slowest + 0.01, 0.99);
    }

    private static (GraphDriver Driver, VoiceConversation Conversation) Replay()
    {
        var conversation = new VoiceConversation();
        var node = new ReplayNode("replay", 78, conversation, new VoicePhrases(Languages), GodModeGraph.Playback(Languages));
        return (new GraphDriver(node), conversation);
    }

    /// <summary>The line, cut by the user in its third sentence.</summary>
    private static SpokenUtterance CutInThird(SpokenUtteranceState state = SpokenUtteranceState.Interrupted) =>
        new(Line, [First, Second, Third], state)
        {
            Heard = new Heard($"{First} {Second} Tredje", "sætning …", 21, 30) { FirstUnheardSentence = 2 },
        };

    private static SpokenUtterance Finished() => new(Line, [First, Second, Third], SpokenUtteranceState.Finished);

    [Theory]
    [InlineData("Gentag.")]
    [InlineData("Hvad sagde du?")]
    [InlineData("Gentag det.")]
    [InlineData("Repeat that.")]
    public async Task Repeat_after_a_cut_says_it_from_the_cut_sentence(string asked)
    {
        var (driver, _) = Replay();
        driver.Context.LastSpeech = CutInThird();

        Assert.Equal(Third, (await driver.SayAsync(asked))?.ResponseText);
    }

    [Fact]
    public async Task Repeat_of_a_line_said_to_its_end_says_all_of_it()
    {
        var (driver, _) = Replay();
        driver.Context.LastSpeech = Finished();

        Assert.Equal(Line, (await driver.SayAsync("Gentag."))?.ResponseText);
    }

    [Theory]
    [InlineData("Spol tilbage.")]
    [InlineData("Lidt tilbage.")]
    [InlineData("Back up.")]
    [InlineData("Rewind.")]
    public async Task Back_up_says_it_from_a_sentence_before_the_cut(string asked)
    {
        var (driver, _) = Replay();
        driver.Context.LastSpeech = CutInThird();

        Assert.Equal($"{Second} {Third}", (await driver.SayAsync(asked))?.ResponseText);
    }

    [Fact]
    public async Task Back_up_after_a_line_said_to_its_end_says_its_last_sentence()
    {
        var (driver, _) = Replay();
        driver.Context.LastSpeech = Finished();

        Assert.Equal(Third, (await driver.SayAsync("Spol tilbage."))?.ResponseText);
    }

    [Fact]
    public async Task From_the_start_with_no_reply_being_read_says_the_whole_line()
    {
        var (driver, _) = Replay();
        driver.Context.LastSpeech = CutInThird();

        Assert.Equal(Line, (await driver.SayAsync("Fra starten."))?.ResponseText);
    }

    [Fact]
    public async Task From_the_start_and_the_paragraph_of_a_reply_being_read_are_its_parts_as_said()
    {
        var (driver, conversation) = Replay();
        driver.Context.LastSpeech = Finished();
        var project = new ProjectRef("server-a", "p/r/master");
        conversation.Reading = new ReplyReading(project, ["en", "to", "tre"], 2, 1, [])
        {
            Said = System.Collections.Immutable.ImmutableDictionary<int, string>.Empty.Add(0, "Master, del 1.").Add(1, "Master, del 2."),
        };

        Assert.Equal("Master, del 2.", (await driver.SayAsync("Gentag afsnittet."))?.ResponseText);
        Assert.Equal(2, Assert.IsType<ReplyReading>(conversation.Reading).Next);
        Assert.Equal("Master, del 1.", (await driver.SayAsync("Fra starten."))?.ResponseText);
        // "Mere" goes on from the second part
        Assert.Equal(1, Assert.IsType<ReplyReading>(conversation.Reading).Next);
        Assert.Equal(project, conversation.Current);
    }

    [Fact]
    public async Task Repeat_before_anything_was_said_says_so()
    {
        var (driver, _) = Replay();

        Assert.Equal("Jeg har ikke sagt noget endnu.", (await driver.SayAsync("Gentag."))?.ResponseText);
    }

    /// <summary>A bare "tilbage" is go_back's (#287), and a command with more words in it is the model's.</summary>
    [Theory]
    [InlineData("Tilbage.")]
    [InlineData("Back.")]
    [InlineData("Gentag svaret til 283.")]
    [InlineData("Hvad sagde 283?")]
    [InlineData("Pause.")]
    [InlineData("Vent.")]
    public async Task Other_words_are_not_the_nodes(string said)
    {
        var (driver, _) = Replay();
        driver.Context.LastSpeech = CutInThird();

        Assert.Null(await driver.SayAsync(said));
    }

    [Fact]
    public async Task A_partial_is_never_the_nodes()
    {
        var (driver, _) = Replay();
        driver.Context.LastSpeech = CutInThird();
        driver.Context.LatestTranscription = new TranscriptionEvent { Text = "Gentag", IsPartial = true };
        driver.Context.CleanedText = "Gentag";

        Assert.Null(await driver.Graph.EvaluateAsync(driver.Context, CancellationToken.None));
    }

    /// <summary>"Fortsæt" with nothing being said goes on with a line that was cut, from its cut sentence; with nothing cut, it is the model's.</summary>
    [Fact]
    public async Task Resume_goes_on_with_a_cut_line_and_is_the_models_otherwise()
    {
        var (driver, _) = Replay();
        driver.Context.LastSpeech = CutInThird();
        Assert.Equal(Third, (await driver.SayAsync("Fortsæt."))?.ResponseText);

        driver.Context.LastSpeech = Finished();
        Assert.Null(await driver.SayAsync("Fortsæt."));
    }

    /// <summary>"Langsommere" and "hurtigere" between readings change the speed of what is said from then on, by VoiceBot's step.</summary>
    [Fact]
    public async Task Slower_and_faster_between_readings_change_the_sessions_speed()
    {
        var (driver, _) = Replay();

        Assert.Equal("Langsommere.", (await driver.SayAsync("Langsommere."))?.ResponseText);
        Assert.Equal(1 / PlaybackCommands.DefaultSpeedStep, driver.Context.Speech.Speed, 6);
        Assert.Equal("Hurtigere.", (await driver.SayAsync("Hurtigere."))?.ResponseText);
        Assert.Equal(1.0, driver.Context.Speech.Speed, 6);

        driver.Context.Speech.Speed = VoiceBot.Core.Graph.SpeechControl.MinSpeed;
        Assert.Equal("Langsommere går det ikke.", (await driver.SayAsync("Langsommere."))?.ResponseText);
    }
}
