using GodMode.Shared.Models;
using VoiceBot.Core.Pipeline;
using VoiceBot.Core.Resources;
using VoiceBot.Testing;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// The "heard you" tone (#458): a short, quiet note as a final is taken as a turn, before anything is said back, so the
/// user knows the bot has it during the seconds before it speaks. None for a final VoiceBot drops (echo, a duplicate,
/// noise), none over speech, and none when the setting is off.
/// </summary>
public sealed class HeardToneTests
{
    private const string Server = "server-a";
    private static readonly SessionLanguages Danish = new("da-DK");

    /// <summary>Longer than anything the bot plays in these tests (its 50 ms of speech, after an earcon of at most 0.6 s): the speaker is quiet after it.</summary>
    private static readonly TimeSpan PlayedOut = TimeSpan.FromMilliseconds(800);

    private static int ToneBytes(RecordingAudioSink speaker) => Earcons.Heard(speaker.Format).Length;

    private static int Tones(RecordingAudioSink speaker) => speaker.Calls.Count(c => c.Kind == SinkCallKind.Audio && c.AudioBytes == ToneBytes(speaker));

    private static string Described(RecordingAudioSink speaker) =>
        string.Join(", ", speaker.Calls.Select(c => c.Kind == SinkCallKind.Audio ? c.AudioBytes == ToneBytes(speaker) ? "tone" : $"audio {c.AudioBytes}" : c.Kind.ToString()));

    /// <summary>A session that has greeted, and whose greeting has played out.</summary>
    private static async Task<(OfflineVoice Voice, RecordingAudioSink Speaker)> GreetedAsync(ScriptedChatClient model,
        VoiceSettings? settings = null, FakeServers? servers = null, TimeSpan? speech = null)
    {
        var speaker = new RecordingAudioSink();
        var voice = await OfflineVoice.StartAsync(servers ?? new FakeServers(), model, settings: settings, speaker: speaker, speech: speech);
        await voice.Events.SaidAsync("Klar.");
        await Eventually.UntilAsync(() => speaker.Calls.Any(c => c.Kind == SinkCallKind.Audio), () => "the greeting's audio");
        await Task.Delay(speech is null ? PlayedOut : TimeSpan.Zero);
        return (voice, speaker);
    }

    [Fact]
    public void The_tone_is_one_short_quiet_note_unlike_the_earcons()
    {
        var format = new VoiceBot.Core.Audio.AudioFormat(16_000, 16, 1);
        var tone = Earcons.Heard(format);

        Assert.InRange(tone.Length / (double)format.BytesPerSecond, 0.05, 0.1);
        var loudest = Enumerable.Range(0, tone.Length / 2).Max(i => Math.Abs((int)BitConverter.ToInt16(tone, i * 2)));
        Assert.InRange(loudest, short.MaxValue * Earcons.HeardVolume * 0.9, short.MaxValue * Earcons.HeardVolume);
        Assert.True(Earcons.HeardVolume < Earcons.Volume);
        Assert.DoesNotContain(Enum.GetValues<Earcon>(), e => Earcons.Pcm(e, format).Length == tone.Length);
    }

    /// <summary>The issue's first test: the tone, then the answer's speech.</summary>
    [Fact]
    public async Task A_final_the_graph_takes_gets_the_tone_on_the_sink_before_any_speech()
    {
        var (voice, speaker) = await GreetedAsync(new ScriptedChatClient().Respond("Intet venter på dig."));
        await using var _ = voice;
        var greeting = speaker.Calls.Count;

        voice.Transcriptions.SayAsRecognized("Hvad venter på mig?");
        await voice.Events.SaidAsync("Intet venter på dig.");
        await Eventually.UntilAsync(() => speaker.Calls.Skip(greeting).Count(c => c.Kind == SinkCallKind.Audio) >= 2, () => Described(speaker));

        var after = speaker.Calls.Skip(greeting).Where(c => c.Kind == SinkCallKind.Audio).ToList();
        Assert.Equal(ToneBytes(speaker), after[0].AudioBytes);
        Assert.All(after.Skip(1), c => Assert.NotEqual(ToneBytes(speaker), c.AudioBytes));
        Assert.Equal(1, Tones(speaker));
    }

    /// <summary>
    /// The issue's second test: the bot's own words heard back are dropped as echo, with no tone. The final after it is
    /// taken, and gets its tone, so the echo was read and passed over.
    /// </summary>
    [Fact]
    public async Task A_final_dropped_as_echo_gets_no_tone()
    {
        const string Said = "Issue 283 venter på dit svar om push til master.";
        var (voice, speaker) = await GreetedAsync(new ScriptedChatClient().Respond(Said).Respond("Godt."));
        await using var _ = voice;

        voice.Transcriptions.SayAsRecognized("Hvad venter på mig?");
        await voice.Events.SaidAsync(Said);
        await Task.Delay(PlayedOut);
        voice.Transcriptions.AddFinal(Said);
        voice.Transcriptions.SayAsRecognized("Hvad med resten?");
        await voice.Events.SaidAsync("Godt.");

        Assert.DoesNotContain(Said, voice.Events.Transcripts);
        Assert.Equal(2, Tones(speaker));
    }

    [Fact]
    public async Task A_final_delivered_twice_and_noise_get_no_tone()
    {
        var noise = VoiceSession.NoiseWords(Danish).First();
        var (voice, speaker) = await GreetedAsync(new ScriptedChatClient().Respond("Intet.").Respond("Godt."));
        await using var _ = voice;

        voice.Transcriptions.SayAsRecognized("Hvad venter på mig?");
        await voice.Events.SaidAsync("Intet.");
        await Task.Delay(PlayedOut);
        // The same final again, no partial between: delivered twice. Then a ghost word
        voice.Transcriptions.AddFinal("Hvad venter på mig?");
        voice.Transcriptions.AddFinal(noise);
        voice.Transcriptions.SayAsRecognized("Og nu?");
        await voice.Events.SaidAsync("Godt.");

        Assert.Equal(["Hvad venter på mig?", "Og nu?"], voice.Events.Transcripts);
        Assert.Equal(2, Tones(speaker));
    }

    /// <summary>A final that cuts the bot off: its speech is interrupted, then the tone says the user has the turn.</summary>
    [Fact]
    public async Task A_final_that_barges_in_gets_the_tone_after_the_interrupt()
    {
        var (voice, speaker) = await GreetedAsync(new ScriptedChatClient().Respond("Godt."), speech: TimeSpan.FromSeconds(5));
        await using var _ = voice;

        voice.Transcriptions.SayAsRecognized("Stille et øjeblik, hvad venter på mig?");
        await voice.Events.SaidAsync("Godt.");
        await Eventually.UntilAsync(() => Tones(speaker) == 1, () => Described(speaker));

        var calls = speaker.Calls.ToList();
        var tone = calls.FindIndex(c => c.Kind == SinkCallKind.Audio && c.AudioBytes == ToneBytes(speaker));
        Assert.Contains(calls.Take(tone), c => c.Kind == SinkCallKind.Interrupt);
    }

    /// <summary>
    /// Dictation (#459): its start and its terminator get the tone, its parts none: a sound at every pause to think would
    /// break the thought, and the read-back says what was heard.
    /// </summary>
    [Fact]
    public async Task A_dictation_gets_the_tone_on_its_start_and_its_send_never_on_its_parts()
    {
        var servers = new FakeServers();
        var speaker = new RecordingAudioSink();
        await using var voice = await OfflineVoice.StartAsync(servers, new ScriptedChatClient(), speaker: speaker,
            connect: _ => { servers.Set(Server, Question("p/r/283", "283-voice", "Skal jeg pushe?")); return Task.CompletedTask; });
        await voice.Events.SaidAsync("issue 283, voice, har et spørgsmål.");
        await Task.Delay(PlayedOut);

        voice.Transcriptions.SayAsRecognized("Diktér til 283.");
        await voice.Events.SaidAsync("Diktat til issue 283. Sig send, eller annullér.");
        await Task.Delay(PlayedOut);
        voice.Transcriptions.SayAsRecognized("Brug den eksisterende migration.");
        voice.Transcriptions.SayAsRecognized("Og skriv en note.");
        await Eventually.UntilAsync(() => voice.Events.Transcripts.Count == 3, () => "the parts");
        await Task.Delay(PlayedOut);
        Assert.True(Tones(speaker) == 1, Described(speaker));

        voice.Transcriptions.SayAsRecognized("Send.");
        await Eventually.UntilAsync(() => servers.Replies.Count == 1, () => "the dictation sent");

        Assert.Equal(2, Tones(speaker));
    }

    [Fact]
    public async Task With_the_setting_off_neither_the_tone_nor_an_earcon_plays()
    {
        var servers = new FakeServers();
        var (voice, speaker) = await GreetedAsync(new ScriptedChatClient().Respond("Intet."), VoiceSettings.Default with { Earcons = false }, servers);
        await using var _ = voice;

        voice.Transcriptions.SayAsRecognized("Hvad venter på mig?");
        await voice.Events.SaidAsync("Intet.");
        servers.Set(Server, new AttentionItem("p/r/283", "283-voice", "Default", "root", Shared.Enums.AttentionKind.Error,
            DateTime.UtcNow.AddMinutes(-5), "Build failed."));
        await voice.Events.SaidAsync("issue 283, voice, fejlede.");
        await Task.Delay(PlayedOut);

        var failed = Earcons.Pcm(Earcon.Failed, speaker.Format).Length;
        Assert.Equal(0, Tones(speaker));
        Assert.DoesNotContain(speaker.Calls, c => c.AudioBytes == failed);
    }

    [Fact]
    public async Task The_tone_plays_only_when_the_speaker_is_quiet()
    {
        var time = new ManualTime();
        var speaker = new RecordingAudioSink();
        var sink = new CueingSink(speaker, time: time);
        var second = new byte[speaker.Format.BytesPerSecond];

        Assert.True(await sink.PlayIfQuietAsync(Earcons.Heard(speaker.Format)));
        await sink.SendAudioAsync(second, CancellationToken.None);
        // The tone and a second of speech play on, one after the other
        time.Advance(TimeSpan.FromMilliseconds(900));
        Assert.False(await sink.PlayIfQuietAsync(Earcons.Heard(speaker.Format)));
        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.True(await sink.PlayIfQuietAsync(Earcons.Heard(speaker.Format)));

        // An interrupt ends what was sent
        await sink.SendAudioAsync(second, CancellationToken.None);
        await sink.InterruptAsync(CancellationToken.None);
        Assert.True(await sink.PlayIfQuietAsync(Earcons.Heard(speaker.Format)));

        // A cue waits for its line, which is about to be said: nothing plays before it
        time.Advance(TimeSpan.FromSeconds(1));
        sink.Cue(Earcon.Done);
        Assert.False(await sink.PlayIfQuietAsync(Earcons.Heard(speaker.Format)));

        Assert.Equal(3, speaker.Calls.Count(c => c.AudioBytes == Earcons.Heard(speaker.Format).Length));
    }

    [Fact]
    public async Task Speech_sent_while_the_tone_is_sent_waits_for_it()
    {
        var release = new TaskCompletionSource();
        var speaker = new GatedSink(release.Task);
        var sink = new CueingSink(speaker);

        var tone = sink.PlayIfQuietAsync(Earcons.Heard(speaker.Format));
        var speech = sink.SendAudioAsync(new byte[100], CancellationToken.None);
        Assert.False(speech.IsCompleted);
        release.SetResult();
        await Task.WhenAll(tone, speech);

        Assert.Equal([Earcons.Heard(speaker.Format).Length, 100], speaker.Sent);
    }

    /// <summary>A speaker whose sends complete when the test says.</summary>
    private sealed class GatedSink(Task gate) : VoiceBot.Core.Audio.IAudioSink
    {
        public List<int> Sent { get; } = [];
        public VoiceBot.Core.Audio.AudioFormat Format => VoiceBot.Core.Audio.AudioFormat.Pcm16kHz;

        public async Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct)
        {
            lock (Sent) Sent.Add(audio.Length);
            await gate;
        }

        public Task SendStatusAsync(string message, CancellationToken ct) => Task.CompletedTask;
        public Task InterruptAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
