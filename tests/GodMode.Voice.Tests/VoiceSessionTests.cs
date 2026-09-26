using GodMode.Shared.Models;
using VoiceBot.Core.Pipeline;
using VoiceBot.Core.Resources;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>A whole voice session over servers in memory: what it announces, and where an answer goes.</summary>
public sealed class VoiceSessionTests
{
    private const string ServerA = "server-a";
    private const string ServerB = "server-b";

    [Fact]
    public async Task What_waits_at_the_start_is_announced_after_the_greeting_and_a_new_item_when_it_comes()
    {
        var servers = new FakeServers();
        await using var voice = await OfflineVoice.StartAsync(servers, new ScriptedModel(),
            connect: _ => { servers.Set(ServerA, Question("p/r/101-cleanup", "101-cleanup", "Skal jeg slette de gamle kolonner?")); return Task.CompletedTask; });

        await voice.Events.SaidAsync("101 har et spørgsmål.");
        servers.Set(ServerB, Permission("p/r/283-voice", "283-voice", "Bash: git push origin feature/283"));
        await voice.Events.SaidAsync("283 skal have tilladelse: Bash: git push origin feature/283. Svar på skærmen.");

        Assert.Equal(["Klar.", "101 har et spørgsmål.", "283 skal have tilladelse: Bash: git push origin feature/283. Svar på skærmen."], voice.Events.Responses);
    }

    [Fact]
    public async Task An_item_is_announced_once_however_often_its_list_is_pushed_again()
    {
        var servers = new FakeServers();
        var item = Question("p/r/101", "101-cleanup", "Hvilken branch?");
        await using var voice = await OfflineVoice.StartAsync(servers, new ScriptedModel());

        servers.Set(ServerA, item);
        await voice.Events.SaidAsync("101 har et spørgsmål.");
        servers.Set(ServerA, item);   // a reconnect takes the whole list again
        servers.Set(ServerA, item, Question("p/r/102", "102-docs", "Dansk eller engelsk?", minutesAgo: 1));
        await voice.Events.SaidAsync("102 har et spørgsmål.");

        Assert.Single(voice.Events.Responses, r => r == "101 har et spørgsmål.");
    }

    [Fact]
    public async Task Several_at_once_are_said_after_their_count()
    {
        var servers = new FakeServers();
        await using var voice = await OfflineVoice.StartAsync(servers, new ScriptedModel(), connect: _ =>
        {
            servers.Set(ServerA, Question("p/r/101", "101-a", "?"), Question("p/r/102", "102-b", "?"));
            servers.Set(ServerB, Question("p/r/103", "103-c", "?"));
            return Task.CompletedTask;
        });

        await voice.Events.SaidAsync("3 venter på dig: 101 har et spørgsmål. 102 har et spørgsmål. 103 har et spørgsmål.");
    }

    /// <summary>
    /// Answer routing: an answer that names no project goes to the one announced last, not to another that also
    /// waits. The model is scripted to leave the project out, as the prompt tells it to for "Svar at …".
    /// </summary>
    [Fact]
    public async Task An_answer_that_names_no_project_goes_to_the_one_announced()
    {
        var servers = new FakeServers();
        var model = new ScriptedModel()
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.TextParameter] = "Brug den eksisterende migration." })
            .Respond("Sendt til 283.");
        await using var voice = await OfflineVoice.StartAsync(servers, model,
            connect: _ => { servers.Set(ServerA, Question("p/r/101", "101-cleanup", "Slet kolonnerne?", minutesAgo: 30)); return Task.CompletedTask; });
        await voice.Events.SaidAsync("101 har et spørgsmål.");
        servers.Set(ServerB, Question("p/r/283", "283-voice", "Ny migration eller den eksisterende?"));
        await voice.Events.SaidAsync("283 har et spørgsmål.");

        voice.Transcriptions.Say("Svar at den skal bruge den eksisterende migration");
        await voice.Events.SaidAsync("Sendt til 283.");

        var (project, text) = Assert.Single(servers.Replies);
        Assert.Equal(new ProjectRef(ServerB, "p/r/283"), project);
        Assert.Equal("Brug den eksisterende migration.", text);
    }

    [Fact]
    public async Task An_answer_names_a_project_by_its_number_said_in_Danish()
    {
        var servers = new FakeServers();
        var model = new ScriptedModel()
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.ProjectParameter] = "hundrede og et", [VoiceTools.TextParameter] = "Ja, slet dem." })
            .Respond("Sendt til 101.");
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: _ =>
        {
            servers.Set(ServerA, Question("p/r/101", "101-cleanup", "Slet kolonnerne?", minutesAgo: 30), Question("p/r/283", "283-voice", "Migration?"));
            return Task.CompletedTask;
        });
        await voice.Events.SaidAsync("2 venter på dig: 101 har et spørgsmål. 283 har et spørgsmål.");

        voice.Transcriptions.Say("Svar hundrede og et at den skal slette dem");
        await voice.Events.SaidAsync("Sendt til 101.");

        Assert.Equal(new ProjectRef(ServerA, "p/r/101"), Assert.Single(servers.Replies).Project);
    }

    /// <summary>No permission is granted or denied by voice: ReplyAndResume would deny one with the text.</summary>
    [Fact]
    public async Task A_permission_request_is_not_answered_by_voice()
    {
        var servers = new FakeServers();
        var model = new ScriptedModel()
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.TextParameter] = "Ja, gør det." })
            .Respond("283 skal have tilladelse. Svar på skærmen.");
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: _ =>
        {
            servers.Set(ServerA, Permission("p/r/283", "283-voice", "Bash: rm -rf build"));
            return Task.CompletedTask;
        });
        await voice.Events.SaidAsync("283 skal have tilladelse: Bash: rm -rf build. Svar på skærmen.");

        voice.Transcriptions.Say("ja gør det");
        await voice.Events.SaidAsync("283 skal have tilladelse. Svar på skærmen.");

        Assert.Empty(servers.Replies);
        Assert.Contains(model.ToolResults, r => r.Contains("answered on screen") && r.Contains("Nothing was sent"));
    }

    /// <summary>"ja" and "nej" are answers: no noise filter may drop them before they reach the model.</summary>
    [Theory]
    [InlineData("ja")]
    [InlineData("nej")]
    [InlineData("tak")]
    public async Task A_spoken_yes_or_no_reaches_the_model(string answer)
    {
        var model = new ScriptedModel().Respond("Klar.");
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.Say(answer);

        await Eventually.UntilAsync(() => model.UserTexts.Any(t => t.EndsWith($"Text: {answer}")),
            () => $"the model to get \"{answer}\"; it got: {string.Join(" | ", model.UserTexts)}");
    }

    [Fact]
    public void No_noise_word_is_an_answer() =>
        Assert.DoesNotContain(VoiceSession.NoiseWords, VoiceSession.AnswerWords.Contains);

    /// <summary>VoiceBot stops announcing for good when its formatter throws (johnjuuljensen/VoiceBot#27).</summary>
    [Fact]
    public void A_formatter_that_throws_once_does_not_end_the_announcements()
    {
        var formatter = new NeverThrowingFormatter(new ThrowsOnce(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.Equal("101 har et spørgsmål.", formatter.Format([new Announcement("101 har et spørgsmål")], new SessionLanguages("da-DK")));
        Assert.Equal("fine: 102", formatter.Format([new Announcement("102")], new SessionLanguages("da-DK")));
    }

    [Fact]
    public async Task Announcements_go_on_for_the_whole_session()
    {
        var servers = new FakeServers();
        await using var voice = await OfflineVoice.StartAsync(servers, new ScriptedModel());

        for (var n = 101; n <= 104; n++)
        {
            servers.Set(ServerA, Question($"p/r/{n}", $"{n}-x", "?"));
            await voice.Events.SaidAsync($"{n} har et spørgsmål.");
        }
    }

    private sealed class ThrowsOnce : IAnnouncementFormatter
    {
        private int _calls;

        public string Format(IReadOnlyList<Announcement> announcements, SessionLanguages languages) =>
            Interlocked.Increment(ref _calls) == 1
                ? throw new InvalidOperationException("formatter bug")
                : $"fine: {announcements[0].Text}";
    }
}
