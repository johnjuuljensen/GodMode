using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Providers.ElevenLabs;

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

    /// <summary>
    /// A chat has no issue number: its name's word, else its kind, then its kind numbered. The date and suffix of its
    /// id ("260930-chat-testing-lgp2") are never a handle: handles come from the name, not the id.
    /// </summary>
    [Fact]
    public void A_chat_with_no_issue_number_is_named_by_its_name_else_its_kind()
    {
        var handles = new ProjectHandles();

        Assert.Equal("testing", handles.For(new ProjectRef("a", "Outbound/Assistant/260930-chat-testing-lgp2"), "testing", "Assistant", "chat"));
        Assert.Equal("chat", handles.For(new ProjectRef("a", "Outbound/Assistant/260930-chat-chat-k7q2"), "chat", "Assistant", "chat"));
        Assert.Equal("chat 2", handles.For(new ProjectRef("a", "Outbound/Assistant/260930-chat-chat-m3p9"), "Chat", "Assistant", "chat"));
        Assert.Equal("testing 2", handles.For(new ProjectRef("b", "Outbound/Assistant/260930-chat-testing-lgp2"), "testing", "Assistant", "chat"));
        Assert.Equal("experiment", handles.For(new ProjectRef("a", "Private/lab/260930-experiment-x-a1b2"), "x", "lab", "experiment"));
    }

    [Theory]
    [InlineData("Assistant")]
    [InlineData("assistant")]
    [InlineData("Assistent")]
    [InlineData("chat")]
    public void A_root_or_kind_only_one_project_has_names_it(string spoken)
    {
        var handles = new ProjectHandles();
        var chat = new ProjectRef("a", "Outbound/Assistant/260930-chat-testing-lgp2");
        handles.For(chat, "testing", "Assistant", "chat");
        handles.For(new ProjectRef("a", "Work/godmode/283"), "feature/283-voice-on-windows", "godmode", "issue");

        Assert.Equal(chat, handles.Resolve(spoken));
    }

    [Fact]
    public void A_root_several_projects_have_names_none_but_their_handles_do()
    {
        var handles = new ProjectHandles();
        var testing = new ProjectRef("a", "Outbound/Assistant/260930-chat-testing-lgp2");
        var invoices = new ProjectRef("a", "Outbound/Assistant/260930-chat-invoices-k7q2");
        handles.For(testing, "testing", "Assistant", "chat");
        handles.For(invoices, "invoices", "Assistant", "chat");

        Assert.Null(handles.Resolve("Assistant"));
        Assert.Null(handles.Resolve("Assistent"));
        Assert.Null(handles.Resolve("chat"));
        Assert.Equal(testing, handles.Resolve("testing"));
        Assert.Equal(invoices, handles.Resolve("invoices"));
    }

    /// <summary>A number after a stem no handle has is no numbered handle: "issue 283" is 283, as its words say.</summary>
    [Fact]
    public void A_number_after_a_word_no_handle_has_names_the_project_with_that_number()
    {
        var handles = new ProjectHandles();
        var voice = new ProjectRef("a", "Work/godmode/283");
        handles.For(voice, "feature/283-voice-on-windows", "godmode", "issue");
        handles.For(new ProjectRef("a", "Outbound/Assistant/260930-chat-chat-a1"), "chat", "Assistant", "chat");

        Assert.Equal(voice, handles.Resolve("issue 283"));
        Assert.Null(handles.Resolve("chat 283"));
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
    public void Keyterms_are_the_command_words_then_the_roots_profiles_and_handles_within_ElevenLabs_limits()
    {
        var handles = new ProjectHandles();
        var now = DateTime.UtcNow;
        ServerProject Project(int minutesAgo, string id, string name, string root, string profile) =>
            new("a", "a", new ProjectSummary(id, name, ProjectState.Idle, now.AddMinutes(-minutesAgo), RootName: root, ProfileName: profile, Kind: "chat"));
        List<ServerProject> projects =
        [
            Project(0, "Outbound/Assistant/1", "Kappe", "Assistant", "Outbound"),
            Project(1, "Work/server-configs/2", "feature/283-voice", "server-configs", "Work"),
            Project(2, "Work/a-root-name-over-twenty-characters/3", "Vonage", "a-root-name-over-twenty-characters", "Work"),
            .. Enumerable.Range(0, 60).Select(i => Project(10 + i, $"Outbound/Assistant/x{i}", $"handle{(char)('a' + i / 26)}{(char)('a' + i % 26)}", "Assistant", "Outbound")),
        ];
        foreach (var p in projects) handles.For(p.Ref, p.Project.Name, p.Project.RootName, p.Project.Kind);

        var keyterms = VoiceSession.Keyterms(projects, handles);

        Assert.Equal(ElevenLabsLanguageOptions.MaxRealtimeKeyterms, keyterms.Count);
        Assert.All(keyterms, t => Assert.True(t.Length <= ElevenLabsLanguageOptions.MaxRealtimeKeytermLength, t));
        Assert.Equal(
            [.. GodModeGraph.CommandWords, "Assistant", "server-configs", "Outbound", "Work", "kappe", "vonage", "handleaa"],
            keyterms.Take(GodModeGraph.CommandWords.Count + 7));
        // A handle that is a number is recognized as it is; a name too long to be a keyterm is left out, not cut
        Assert.DoesNotContain("283", keyterms);
        Assert.DoesNotContain(keyterms, t => t.StartsWith("a-root-name", StringComparison.Ordinal));
        Assert.Equal(keyterms.Count, keyterms.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>A Jira key is no issue number (#529): "FE86" got the handle 86, said "issue 86".</summary>
    [Theory]
    [InlineData("FE86", "fe86", "chat fe86")]
    [InlineData("FE-86", "fe-86", "chat fe-86")]
    [InlineData("BD-123 backup job", "bd-123", "chat bd-123")]
    [InlineData("feature/283-voice-on-windows", "283", "issue 283")]
    [InlineData("issue_283", "283", "issue 283")]
    public void A_number_is_an_issue_only_when_it_stands_alone(string name, string handle, string label)
    {
        var handles = new ProjectHandles();
        var project = new ProjectRef("a", "Mega/Mega-Assistant/1");

        Assert.Equal(handle, handles.For(project, name, "Mega-Assistant", "chat", "Mega"));
        Assert.Equal(label, handles.LabelOf(project));
    }

    /// <summary>The model's label for a session (#529): its kind and a word of its name, with its root and profile.</summary>
    [Theory]
    [InlineData("chat FE86 in Mega-Assistant, profile Mega")]
    [InlineData("chat FE86")]
    [InlineData("chat fe 86")]
    [InlineData("FE86")]
    [InlineData("FE 86")]
    public void A_session_is_found_by_its_kind_and_a_word_of_its_name(string spoken)
    {
        var handles = new ProjectHandles();
        var fe86 = new ProjectRef("a", "Mega/Mega-Assistant/1");
        handles.For(fe86, "FE-86", "Mega-Assistant", "chat", "Mega");
        handles.For(new ProjectRef("a", "Mega/Mega-Assistant/2"), "general", "Mega-Assistant", "chat", "Mega");

        Assert.Equal(fe86, handles.Resolve(spoken));
    }

    [Fact]
    public void A_kind_and_a_word_two_projects_share_names_neither()
    {
        var handles = new ProjectHandles();
        handles.For(new ProjectRef("a", "Mega/Assistant/1"), "nightly backup", "Assistant", "chat", "Mega");
        handles.For(new ProjectRef("a", "Mega/Assistant/2"), "weekly backup", "Assistant", "chat", "Mega");

        Assert.Null(handles.Resolve("chat backup"));
    }
}
