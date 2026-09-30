using VoiceBot.Core.Tools;

namespace GodMode.Voice.Tests;

/// <summary>"Hjælp": what the user can ask, said on the first partial, without the model.</summary>
public sealed class HelpTests
{
    private static readonly string Danish = HelpNode.Say(GraphTools(), danish: true);
    private static readonly string English = HelpNode.Say(GraphTools(), danish: false);

    /// <summary>The names of the tools GodMode's graph has.</summary>
    private static IReadOnlyCollection<string> GraphTools()
    {
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var tools = new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, new VoiceConversation());
        return [.. GodModeGraph.AddTools(new ToolSet(), tools).ResolveAll().Keys];
    }

    /// <summary>The voice log of 2026-09-30: a partial "Hjælp.", then, with no final, nothing answered at all.</summary>
    [Theory]
    [InlineData("Hjælp.", true)]
    [InlineData("Hvad kan du?", true)]
    [InlineData("Hvad kan jeg sige", true)]
    [InlineData("Kommandoer.", true)]
    [InlineData("Help.", false)]
    [InlineData("What can I say?", false)]
    public async Task A_partial_that_asks_for_help_is_answered_with_the_list_and_the_model_is_not_called(string asked, bool danish)
    {
        var model = new ScriptedChatClient();
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.AddPartial(asked);
        await voice.Events.SaidAsync(danish ? Danish : English);

        Assert.Equal(0, model.Calls);
    }

    /// <summary>
    /// The rest of the utterance is help's: the partials that grow from it and the final (here one that merged the
    /// next words, as ElevenLabs does, VoiceBot#62) reach neither the model nor help again. The next utterance does.
    /// </summary>
    [Fact]
    public async Task Help_claims_the_rest_of_its_utterance_and_the_next_one_goes_to_the_model()
    {
        var model = new ScriptedChatClient().Respond("Intet venter.");
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.AddPartial("Hjælp.");
        voice.Transcriptions.AddPartial("Hjælp. Kører");
        voice.Transcriptions.AddFinal("Hjælp. Kører gør man.");
        voice.Transcriptions.SayAsRecognized("Hvad venter?");
        await voice.Events.SaidAsync("Intet venter.");

        Assert.Equal(["Hvad venter?"], model.UserTexts.Select(t => t[(t.IndexOf("Text: ") + "Text: ".Length)..]));
        Assert.Single(voice.Events.Responses, r => r == Danish);
    }

    [Fact]
    public async Task Help_answers_each_time_it_is_asked()
    {
        var model = new ScriptedChatClient();
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Hjælp.");
        voice.Transcriptions.SayAsRecognized("Hjælp.");
        await Eventually.UntilAsync(() => voice.Events.Responses.Count(r => r == Danish) == 2,
            () => $"help said twice; the bot said: {string.Join(" | ", voice.Events.Responses)}");

        Assert.Equal(0, model.Calls);
    }

    /// <summary>Help is the whole utterance: one that only starts with a help word is the model's.</summary>
    [Fact]
    public async Task An_utterance_that_goes_on_past_a_help_word_is_the_models()
    {
        var model = new ScriptedChatClient().Respond("283 venter på et svar.");
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.AddFinal("Hvad kan du fortælle om 283?");
        await voice.Events.SaidAsync("283 venter på et svar.");

        Assert.DoesNotContain(Danish, voice.Events.Responses);
    }

    /// <summary>The list is what the graph can do: every tool it has has its hint, and help says each.</summary>
    [Fact]
    public void The_list_names_every_tool_of_the_graph()
    {
        var tools = GraphTools();

        Assert.Empty(tools.Except(HelpNode.Hints.Select(h => h.Tool)));
        Assert.All(HelpNode.Hints, h => Assert.Contains(h.Danish, Danish));
        Assert.All(HelpNode.Hints, h => Assert.Contains(h.English, English));
        Assert.Equal("Du kan sige: hvad venter, hvilke projekter er der, status og et projekt, svar at og dit svar, læst, stille eller sig til igen.", Danish);
    }

    /// <summary>A tool the graph does not have is not offered.</summary>
    [Fact]
    public void Help_says_only_the_tools_there_are()
    {
        Assert.Equal("Du kan sige: hvad venter eller læst.", HelpNode.Say([VoiceTools.WhatNeedsMe, VoiceTools.MarkSeen, "unknown_tool"], danish: true));
        Assert.Equal("You can say: seen.", HelpNode.Say([VoiceTools.MarkSeen], danish: false));
    }
}
