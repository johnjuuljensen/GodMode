using VoiceBot.Providers.ElevenLabs;

namespace GodMode.Voice.Tests;

/// <summary>
/// GodMode's own words reach the transcriber as keyterms, so "loggen" is not heard as "klokken" or "lokken" (issue #380),
/// and the model reads an obvious mishearing of one as the word that makes sense.
/// </summary>
public sealed class MishearingTests
{
    private const string ServerA = "server-a";

    /// <summary>The words the user said and ElevenLabs heard as something else, or had to be spelled out, and their kin.</summary>
    public static readonly TheoryData<string> GodModeWords =
        ["log", "loggen", "session", "sessionen", "branch", "worktree", "commit", "push", "merge", "issue"];

    [Theory]
    [MemberData(nameof(GodModeWords))]
    public async Task The_transcriber_is_biased_towards_GodMode_words(string word)
    {
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), new ScriptedChatClient());

        Assert.Contains(word, voice.Providers.Language!.SttKeyterms);
        Assert.Contains(word, voice.Session.SttKeyterms);
    }

    /// <summary>
    /// The words go first, with the commands: a server with more names than ElevenLabs takes (50) loses its last
    /// handles, never "loggen".
    /// </summary>
    [Fact]
    public async Task GodMode_words_outrank_the_names_when_the_terms_are_full()
    {
        var servers = new FakeServers();
        for (var i = 0; i < ElevenLabsLanguageOptions.MaxRealtimeKeyterms; i++)
            servers.AddProject(ServerA, $"Work/root{i}/260930-feat-name{i}-a{i}", $"feature/name{i}", root: $"root{i}", kind: "feat", profile: "Work");
        await using var voice = await OfflineVoice.StartAsync(servers, new ScriptedChatClient());

        var terms = voice.Session.SttKeyterms;
        Assert.Equal(ElevenLabsLanguageOptions.MaxRealtimeKeyterms, terms.Count);
        Assert.Equal(GodModeGraph.CommandWords, terms.Take(GodModeGraph.CommandWords.Count));
        Assert.Contains("loggen", terms);
    }

    /// <summary>The prompt says to read a misheard GodMode word as the word, from what the user plausibly meant.</summary>
    [Fact]
    public async Task The_prompt_says_to_correct_an_obvious_mishearing_from_context()
    {
        var model = new ScriptedChatClient().Respond("Uklar.");
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.AddFinal("Jeg skal tjekke lokken.");
        await voice.Events.SaidAsync("Uklar.");

        var prompt = model.Requests.Last().First(m => m.Role == Microsoft.Extensions.AI.ChatRole.System).Text;
        Assert.Contains("MISHEARD WORDS", prompt);
        Assert.Contains("\"loggen\"", prompt);
    }
}
