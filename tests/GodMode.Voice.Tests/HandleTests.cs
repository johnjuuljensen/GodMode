namespace GodMode.Voice.Tests;

/// <summary>Numbers said in Danish, and the short names projects are spoken of by.</summary>
public sealed class HandleTests
{
    [Theory]
    [InlineData("283", 283)]
    [InlineData("to hundrede og treogfirs", 283)]
    [InlineData("tohundredeogtreogfirs", 283)]
    [InlineData("hundrede og tre", 103)]
    [InlineData("et hundrede og tolv", 112)]
    [InlineData("treogtres", 63)]
    [InlineData("femogfyrre", 45)]
    [InlineData("tretten", 13)]
    [InlineData("syv", 7)]
    [InlineData("et tusind og fem", 1005)]
    [InlineData("to tusind tre hundrede og halvtreds", 2350)]
    [InlineData("Halvfems.", 90)]
    public void Danish_numbers_are_read(string spoken, int expected) =>
        Assert.Equal(expected, DanishNumbers.Parse(spoken));

    [Theory]
    [InlineData("vonage")]
    [InlineData("status")]
    [InlineData("to hundrede og vonage")]
    [InlineData("")]
    [InlineData("og")]
    public void Anything_else_is_no_number(string spoken) =>
        Assert.Null(DanishNumbers.Parse(spoken));

    [Fact]
    public void A_project_is_named_by_its_issue_number_else_a_distinctive_word()
    {
        var handles = new ProjectHandles();

        Assert.Equal("283", handles.For(new ProjectRef("a", "p/r/1"), "feature/283-voice-on-windows"));
        Assert.Equal("vonage", handles.For(new ProjectRef("a", "p/r/2"), "Vonage SIP trunk migration"));
        Assert.Equal("recording", handles.For(new ProjectRef("a", "p/r/3"), "fix: recording compliance"));
    }

    [Fact]
    public void Handles_are_unique_across_servers_and_stable_for_the_session()
    {
        var handles = new ProjectHandles();
        var onA = new ProjectRef("server-a", "p/r/283-x");
        var onB = new ProjectRef("server-b", "p/r/283-x");

        var first = handles.For(onA, "283-voice");
        var second = handles.For(onB, "283-voice");

        Assert.Equal("283", first);
        Assert.NotEqual(first, second);
        Assert.Equal(first, handles.For(onA, "a new name the project got since"));
        Assert.Equal(onA, handles.Resolve("283"));
        Assert.Equal(onB, handles.Resolve(second));
    }

    [Fact]
    public void Every_handle_fits_in_an_ElevenLabs_keyterm()
    {
        var handles = new ProjectHandles();

        var handle = handles.For(new ProjectRef("a", "p"), "supercalifragilisticexpialidocious-refactor");
        handles.For(new ProjectRef("a", "q"), "supercalifragilisticexpialidocious-refactor");
        handles.For(new ProjectRef("a", "s"), "supercalifragilisticexpialidocious-refactor");

        Assert.True(handle.Length <= ProjectHandles.MaxLength);
        Assert.All(handles.All, h => Assert.True(h.Length <= ProjectHandles.MaxLength, h));
        Assert.Equal(3, handles.All.Distinct().Count());
    }

    [Theory]
    [InlineData("283")]
    [InlineData("to hundrede og treogfirs")]
    [InlineData("projekt 283")]
    [InlineData("Nummer to hundrede og treogfirs.")]
    public void A_number_said_in_Danish_names_the_project_with_that_handle(string spoken)
    {
        var handles = new ProjectHandles();
        var voice = new ProjectRef("a", "p/r/283");
        handles.For(new ProjectRef("a", "p/r/101"), "101-cleanup");
        handles.For(voice, "283-voice");

        Assert.Equal(voice, handles.Resolve(spoken));
    }

    [Fact]
    public void A_word_of_one_project_name_names_it_and_a_shared_word_names_none()
    {
        var handles = new ProjectHandles();
        var vonage = new ProjectRef("a", "1");
        handles.For(vonage, "Vonage SIP trunk migration");
        handles.For(new ProjectRef("a", "2"), "Nordea payment migration");

        Assert.Equal(vonage, handles.Resolve("trunk"));
        Assert.Equal(vonage, handles.Resolve("Vonnage"));
        Assert.Null(handles.Resolve("migration"));
        Assert.Null(handles.Resolve("ivr"));
    }

    [Fact]
    public void Keyterms_are_the_command_words_and_word_handles_within_ElevenLabs_limits()
    {
        var handles = Enumerable.Range(0, 80).Select(i => $"handle{i}").Prepend("283").Append("a-very-long-handle-over-twenty-characters");

        var keyterms = VoiceSession.Keyterms(handles);

        Assert.True(keyterms.Count <= VoiceBot.Providers.ElevenLabs.ElevenLabsLanguageOptions.MaxRealtimeKeyterms);
        Assert.All(keyterms, t => Assert.True(t.Length <= VoiceBot.Providers.ElevenLabs.ElevenLabsLanguageOptions.MaxRealtimeKeytermLength, t));
        Assert.Equal(GodModeGraph.CommandWords, keyterms.Take(GodModeGraph.CommandWords.Count));
        Assert.DoesNotContain("283", keyterms);
        Assert.Contains("handle0", keyterms);
    }
}
