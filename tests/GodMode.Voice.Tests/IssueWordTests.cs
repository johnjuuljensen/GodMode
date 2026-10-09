using VoiceBot.Core.Resources;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// The voice log of 2026-10-09 (#526): a chat about a new issue started as the issue action, a "needs an issue" that
/// steered the model away, a version number taken for an issue's, a name copied from a list that did not resolve, and
/// rounds whose words were thrown away.
/// </summary>
public sealed class IssueWordTests
{
    private const string ServerA = "server-a";

    private static readonly VoicePhrases Danish = new(new SessionLanguages("da-DK"));

    /// <summary>The user's GodMode root: issues and epics by number, overseers and chats by name and prompt.</summary>
    private static FakeServers Servers() => new FakeServers(ServerA)
        .AddRoot(ServerA, "GodMode", "Godmode", Action("issue", IssueSchema), Action("epic", IssueSchema), Action("overseer"), Action("chat"));

    /// <summary>
    /// 14:16 in the log: "start new GodMode chat", then "I want a new issue on upgrading to Haiku 5.5". The issue word
    /// says what the chat is about: with no number, the draft's chat stays.
    /// </summary>
    [Theory]
    [InlineData("I want a new issue on upgrading to Haiku 5.5")]
    [InlineData("There is no issue. I want an issue created.")]
    [InlineData("Jeg vil have en ny sag om Haiku 5.5")]
    public async Task An_issue_word_with_no_number_keeps_the_drafts_chat(string said)
    {
        var tools = Tools(Servers());
        await Start(tools, root: "GodMode", action: "chat");
        Assert.Equal("chat", tools.Creates.Draft!.Action.Name);

        tools.Creates.Heard(said);
        var result = await Start(tools, action: "issue", name: "Haiku 5.5 upgrade");

        Assert.Contains("the action stays chat: an issue word with no issue number says what the session is about", result);
        Assert.Equal("chat", tools.Creates.TakeProposed()!.Action.Name);
    }

    /// <summary>An issue word with the issue's number does name the issue action.</summary>
    [Fact]
    public async Task An_issue_word_with_its_number_switches_the_draft_to_the_issue()
    {
        var tools = Tools(Servers());
        await Start(tools, root: "GodMode", action: "chat");

        tools.Creates.Heard("Nej, start issue 525 i stedet");
        await Start(tools, action: "issue", issue: "525");

        var request = tools.Creates.TakeProposed()!;
        Assert.Equal(("issue", "525"), (request.Action.Name, request.Issue));
    }

    /// <summary>
    /// The log's "Every action there needs an issue (… : issue)", of a root with chats: it names the one action, says no
    /// number was given, and the actions there that need none.
    /// </summary>
    [Fact]
    public async Task An_issue_action_with_no_number_says_so_and_names_those_that_need_none()
    {
        var tools = Tools(Servers());

        var result = await Start(tools, root: "GodMode", action: "issue");

        Assert.Contains("Action issue in GodMode (profile Godmode) needs an issue number, and the user gave none.", result);
        Assert.Contains("Actions there that need no issue: GodMode (profile Godmode): overseer, chat.", result);
        Assert.DoesNotContain("Every action there needs an issue", result);
    }

    /// <summary>A draft switched to an action that needs an issue, with none said: the draft's action is offered back.</summary>
    [Fact]
    public async Task A_switch_to_an_action_that_needs_a_number_offers_the_drafts_action_back()
    {
        var tools = Tools(Servers());
        await Start(tools, root: "GodMode", action: "chat");

        tools.Creates.Heard("Nej, som epic");
        var result = await Start(tools, action: "epic");

        Assert.Contains("Action epic in GodMode (profile Godmode) needs an issue number, and the user gave none.", result);
        Assert.Contains("The draft had action chat: the user may mean it", result);
    }

    /// <summary>14:19 in the log: the chat "Haiku 5.5 upgrade" was given the handle 5, said "issue 5", beside the real #525.</summary>
    [Theory]
    [InlineData("Haiku 5.5 upgrade", "haiku")]
    [InlineData("Release v1.2.3", "release")]
    [InlineData("feature/283-voice-on-windows", "283")]
    [InlineData("Issue #525: Upgrade to Haiku 5.5", "525")]
    [InlineData("Upgrade to 5.5, see 412", "412")]
    public void A_dotted_version_number_is_no_issue_number(string name, string handle) =>
        Assert.Equal(handle, new ProjectHandles().For(new ProjectRef(ServerA, "p/r/1"), name, kind: "chat"));

    /// <summary>
    /// 14:20 in the log: answer_project(project="issue 5, Haiku in GodMode, profile Godmode"), copied from the waiting
    /// list, was "Unknown project". A name as a tool's result gives it, topic and all, resolves.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void A_name_as_a_tools_result_gives_it_resolves(bool withRoot, bool withProfile)
    {
        var handles = new ProjectHandles();
        var project = new ProjectRef(ServerA, "Godmode/GodMode/1");
        const string Name = "feature/376-mic-timeout";
        handles.For(project, Name, root: "GodMode", kind: "issue", profile: "Godmode");
        handles.For(new ProjectRef(ServerA, "Mega/GodMode/2"), "feature/377-push-to-talk", root: "GodMode", kind: "issue", profile: "Mega");
        var label = handles.LabelOf(project)!;
        var topic = ProjectTopics.Of(Name, "issue", label);
        Assert.NotNull(topic);

        var spoken = new SpokenName(label, withRoot ? "GodMode" : null, withProfile ? "Godmode" : null, topic).ToString();

        Assert.Equal(project, handles.Resolve(spoken));
    }

    /// <summary>"Øh, det…" took an 8 s round for the model to decide to wait: a hesitation alone is waited past by the code.</summary>
    [Theory]
    [InlineData("Øh, det…", true)]
    [InlineData("Øhm.", true)]
    [InlineData("Um, so", true)]
    [InlineData("Hmm", true)]
    [InlineData("det", false)]
    [InlineData("Så", false)]
    [InlineData("Øh, svar 283 ja", false)]
    [InlineData("ja", false)]
    public void A_hesitation_alone_is_one(string said, bool hesitates) =>
        Assert.Equal(hesitates, HesitationNode.Hesitates(said));

    [Fact]
    public async Task A_hesitation_is_waited_past_with_no_model_call()
    {
        var model = new ScriptedChatClient().CallTool(VoiceTools.WhatNeedsMe);
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Øh, det…");
        voice.Transcriptions.SayAsRecognized("Hvad venter?");
        await voice.Events.SaidAsync(Danish.Waiting([]));

        Assert.Equal(1, model.Calls);
        Assert.DoesNotContain(model.UserTexts, t => t.Contains("Øh"));
    }

    private static VoiceTools Tools(FakeServers servers)
    {
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        return new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, new VoiceConversation());
    }

    private static Task<string> Start(VoiceTools tools, string? root = null, string? action = null, string? issue = null, string? name = null) =>
        tools.StartSessionAsync(new CreateAsk(root, action, issue, name, null), CancellationToken.None);
}
