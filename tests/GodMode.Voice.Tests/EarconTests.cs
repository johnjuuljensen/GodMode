using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Audio;
using VoiceBot.Core.Pipeline;
using VoiceBot.Core.Resources;
using VoiceBot.Testing;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// Earcons (#455): a short sound per kind before an announcement (question, done, failed), made in code, played through
/// the session's own speaker before the announcement's words.
/// </summary>
public sealed class EarconTests
{
    private const string Server = "server-a";
    private static readonly SessionLanguages Danish = new("da-DK");

    [Theory]
    [InlineData(AttentionKind.Question, null, Earcon.Question)]
    [InlineData(AttentionKind.Question, TurnOutcome.Blocked, Earcon.Question)]
    [InlineData(AttentionKind.Permission, null, Earcon.Question)]
    [InlineData(AttentionKind.Escalation, null, Earcon.Question)]
    [InlineData(AttentionKind.Review, null, Earcon.Question)]
    [InlineData(AttentionKind.Error, null, Earcon.Failed)]
    [InlineData(AttentionKind.Finished, TurnOutcome.Done, Earcon.Done)]
    [InlineData(AttentionKind.Finished, null, null)]
    public void Each_kind_has_its_earcon_and_idle_none(AttentionKind kind, TurnOutcome? outcome, Earcon? earcon) =>
        Assert.Equal(earcon, Earcons.For(new AttentionItem("p/r/1", "1-x", null, null, kind, DateTime.UtcNow, "x", Outcome: outcome)));

    [Theory]
    [InlineData(16_000, 1)]
    [InlineData(48_000, 2)]
    public void An_earcon_is_a_few_notes_in_the_speakers_format_then_a_gap(int rate, int channels)
    {
        var format = new AudioFormat(rate, 16, channels);
        var sounds = Enum.GetValues<Earcon>().Select(e => Earcons.Pcm(e, format)).ToList();

        foreach (var pcm in sounds)
        {
            Assert.Equal(0, pcm.Length % (2 * channels));
            var seconds = pcm.Length / (double)format.BytesPerSecond;
            Assert.InRange(seconds, 0.3, 0.6);
            // Sound, then the gap's silence at its end
            Assert.Contains(Samples(pcm), s => Math.Abs((int)s) > short.MaxValue * Earcons.Volume / 2);
            Assert.All(Samples(pcm)[^(int)(Earcons.Gap.TotalSeconds * rate * channels)..], s => Assert.Equal(0, s));
        }
        Assert.Equal(sounds.Count, sounds.Select(Convert.ToBase64String).Distinct().Count());
    }

    private static short[] Samples(byte[] pcm) =>
        [.. Enumerable.Range(0, pcm.Length / 2).Select(i => BitConverter.ToInt16(pcm, i * 2))];

    [Fact]
    public async Task A_cue_plays_once_before_the_next_audio()
    {
        var speaker = new RecordingAudioSink();
        var sink = new CueingSink(speaker);
        var cue = Earcons.Pcm(Earcon.Done, speaker.Format);

        sink.Cue(Earcon.Done);
        await sink.SendAudioAsync(new byte[100], CancellationToken.None);
        await sink.SendAudioAsync(new byte[100], CancellationToken.None);

        Assert.Equal([cue.Length, 100, 100], speaker.Calls.Select(c => c.AudioBytes));
    }

    [Fact]
    public async Task An_interrupt_drops_a_cue_not_played()
    {
        var speaker = new RecordingAudioSink();
        var sink = new CueingSink(speaker);

        sink.Cue(Earcon.Question);
        await sink.InterruptAsync(CancellationToken.None);
        await sink.SendAudioAsync(new byte[100], CancellationToken.None);

        Assert.Equal([SinkCallKind.Interrupt, SinkCallKind.Audio], speaker.Calls.Select(c => c.Kind));
        Assert.Equal(100, speaker.Calls[1].AudioBytes);
    }

    private sealed record Board(FakeServers Servers, AttentionBoard Attention, GodModeAnnouncementFormatter Formatter, List<Earcon> Cued);

    private static Board Start()
    {
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var board = new AttentionBoard(servers, handles, projects);
        var conversation = new VoiceConversation();
        var phrases = new VoicePhrases(Danish);
        List<Earcon> cued = [];
        var names = new ProjectNames(projects, handles, conversation);
        return new Board(servers, board, new GodModeAnnouncementFormatter(phrases, conversation, board, names, cued.Add), cued);
    }

    private static Announcement AnnouncementOf(Board board, string projectId) =>
        board.Attention.AnnouncementOf(board.Attention.ItemOf(new ProjectRef(Server, projectId))!, "x");

    [Fact]
    public void Several_said_together_are_cued_by_the_most_urgent()
    {
        var board = Start();
        board.Servers.Set(Server, Finished("p/r/101", "101-cleanup", "Færdig."), Permission("p/r/283", "283-voice", "Bash: git push"));

        board.Formatter.Format([AnnouncementOf(board, "p/r/101"), AnnouncementOf(board, "p/r/283")], Danish);

        Assert.Equal([Earcon.Question], board.Cued);
    }

    [Fact]
    public void An_idle_turn_an_announcement_no_longer_waiting_and_one_of_no_item_cue_nothing()
    {
        var board = Start();
        board.Servers.Set(Server, Finished("p/r/101", "101-cleanup", "Venter.", outcome: null), Question("p/r/283", "283-voice", "?"));
        var answered = AnnouncementOf(board, "p/r/283");
        board.Servers.Set(Server, Finished("p/r/101", "101-cleanup", "Venter.", outcome: null));

        Assert.Equal("issue 101, cleanup, er idle.", board.Formatter.Format([AnnouncementOf(board, "p/r/101")], Danish));
        Assert.Equal("", board.Formatter.Format([answered], Danish));
        board.Formatter.Format([new Announcement("issue 7 er oprettet")], Danish);

        Assert.Empty(board.Cued);
    }

    /// <summary>In a session: the earcon is the speaker's audio right before the announcement's words, and the greeting has none.</summary>
    [Fact]
    public async Task In_a_session_the_earcon_plays_before_the_announcement()
    {
        var servers = new FakeServers();
        var speaker = new RecordingAudioSink();
        await using var voice = await OfflineVoice.StartAsync(servers, new ScriptedChatClient(), speaker: speaker);
        await voice.Events.SaidAsync("Klar.");
        await Eventually.UntilAsync(() => speaker.Calls.Any(c => c.Kind == SinkCallKind.Audio), () => "the greeting's audio");
        var greeting = speaker.Calls.Count;

        servers.Set(Server, Error("p/r/283", "283-voice", "Build failed."));
        await voice.Events.SaidAsync("issue 283, voice, fejlede.");

        var failed = Earcons.Pcm(Earcon.Failed, speaker.Format).Length;
        await Eventually.UntilAsync(() => speaker.Calls.Count(c => c.Kind == SinkCallKind.Audio) > 2,
            () => $"the announcement's audio; the speaker had: {string.Join(", ", speaker.Calls.Select(c => c.AudioBytes))}");
        var calls = speaker.Calls;
        Assert.DoesNotContain(calls.Take(greeting), c => c.AudioBytes == failed);
        Assert.Equal(failed, calls.Skip(greeting).First(c => c.Kind == SinkCallKind.Audio).AudioBytes);
    }

    private static AttentionItem Error(string projectId, string name, string text) =>
        new(projectId, name, "Default", "root", AttentionKind.Error, DateTime.UtcNow.AddMinutes(-5), text);
}
