using VoiceBot.Core.Pipeline;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// The voice log of 2026-10-03 (#473): two epics started as plain issues, an overseer refused that voice can start, and
/// an announcement that dropped a create waiting on its yes. The draft is held in code: the model's later calls fill in
/// what it lacks, and change its kind, root or issue only on the user's own words. The issue's labels pick the action.
/// While a create or its question waits on the user, announcements are held.
/// </summary>
public sealed class DraftTests
{
    private const string ServerA = "server-a";

    /// <summary>The user's GodMode root, as its config is: issues and epics by number, overseers and chats by name and prompt.</summary>
    private static FakeServers Servers() => new FakeServers(ServerA)
        .AddRoot(ServerA, "GodMode", "Godmode", Action("issue", IssueSchema), Action("epic", IssueSchema), Action("overseer"), Action("chat"))
        .AddRoot(ServerA, "provisioning", "Private", Action("new-root", session: false), Action("promote", session: false));

    private static Dictionary<string, object?> Ask(string? root = null, string? action = null, string? issue = null, string? name = null, string? prompt = null)
    {
        Dictionary<string, object?> ask = [];
        if (root is not null) ask[VoiceTools.RootParameter] = root;
        if (action is not null) ask[VoiceTools.ActionParameter] = action;
        if (issue is not null) ask[VoiceTools.IssueParameter] = issue;
        if (name is not null) ask[VoiceTools.NameParameter] = name;
        if (prompt is not null) ask[VoiceTools.PromptParameter] = prompt;
        return ask;
    }

    /// <summary>
    /// #470 in the log: "Launch a GodMode epic", the number asked for, and the model's follow-up call made it an issue.
    /// The switch is refused, the draft stays an epic, and the read-back and the create say epic.
    /// </summary>
    [Fact]
    public async Task The_model_switching_the_drafts_epic_to_an_issue_is_refused_and_the_read_back_says_epic()
    {
        var servers = Servers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.StartSession, Ask(root: "GodMode", action: "epic")).Respond("Hvilket nummer?")
            .CallTool(VoiceTools.StartSession, Ask(action: "issue", issue: "470"));
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Start et GodMode epic");
        await voice.Events.SaidAsync("Hvilket nummer?");
        Assert.Contains("Action epic in GodMode (profile Godmode) needs an issue number, and the user gave none.", voice.ToolResults[0]);
        voice.Transcriptions.SayAsRecognized("Fire, syv, nul.");
        await voice.Events.SaidAsync("Skal jeg oprette issue 470 i GodMode, profil Godmode, som epic?");
        Assert.Contains("the action stays epic: the user did not ask for 'issue'", voice.ToolResults[1]);

        voice.Transcriptions.SayAsRecognized("Ja");
        await voice.Events.SaidAsync("Opretter.");
        await voice.Events.SaidAsync("470 er oprettet.");
        var (root, action, inputs) = Assert.Single(servers.Creates);
        Assert.Equal(("GodMode", "epic"), (root.Root.Name, action));
        Assert.Equal(new Dictionary<string, string> { ["issueNumber"] = "470" }, inputs);
    }

    /// <summary>The user's own words name the new kind ("Nej, som overseer"): the switch is theirs, and taken.</summary>
    [Theory]
    [InlineData("Nej, som overseer")]
    [InlineData("No, as an overseer.")]
    public async Task A_switch_the_users_words_name_is_taken(string said)
    {
        var tools = Tools(Servers());
        await Start(tools, Ask(root: "GodMode", action: "epic"));
        Assert.Equal("epic", tools.Creates.Draft!.Action.Name);

        tools.Creates.Heard(said);
        var result = await Start(tools, Ask(action: "overseer", name: "epic 471", prompt: "Run epic 471."));

        Assert.Contains("Settled: create a session named 'epic 471', the prompt 'Run epic 471.' in GodMode (profile Godmode), action overseer", result);
        Assert.Equal("overseer", tools.Creates.TakeProposed()!.Action.Name);
    }

    /// <summary>Words that name no other kind change nothing: the draft's root and issue stay too, unless the user says the new ones.</summary>
    [Fact]
    public async Task The_drafts_root_and_issue_change_only_on_the_users_words()
    {
        var servers = Servers().AddRoot(ServerA, "kappe", "Kappe", Action("issue", IssueSchema), Action("epic", IssueSchema));
        var tools = Tools(servers);
        await Start(tools, Ask(root: "GodMode", action: "epic", issue: "470"));
        Assert.NotNull(tools.Creates.TakeProposed());

        tools.Creates.Heard("Ja, gør det");
        var kept = await Start(tools, Ask(root: "kappe", action: "epic", issue: "471"));
        Assert.Contains("the root stays GodMode (profile Godmode)", kept);
        Assert.Contains("the issue stays 470", kept);
        var request = tools.Creates.TakeProposed()!;
        Assert.Equal(("GodMode", "470"), (request.Root.Root.Name, request.Issue));

        tools.Creates.Heard("Nej, 471 i kappe");
        var changed = await Start(tools, Ask(root: "kappe", action: "epic", issue: "471"));
        Assert.DoesNotContain("stays", changed);
        request = tools.Creates.TakeProposed()!;
        Assert.Equal(("kappe", "471"), (request.Root.Root.Name, request.Issue));
    }

    /// <summary>A number said digit by digit ("four, seven, one") is the user saying the issue.</summary>
    [Fact]
    public async Task An_issue_said_digit_by_digit_is_the_users_own()
    {
        var tools = Tools(Servers());
        await Start(tools, Ask(root: "GodMode", action: "epic", issue: "470"));

        tools.Creates.Heard("No, four, seven, one.");
        Assert.DoesNotContain("stays", await Start(tools, Ask(issue: "471")));
        Assert.Equal("471", tools.Creates.TakeProposed()!.Issue);
    }

    /// <summary>
    /// The log's announcement: queued between the read-back and the yes, and played before it. It is held until the yes
    /// is answered, and then said: the yes creates.
    /// </summary>
    [Fact]
    public async Task An_announcement_queued_between_the_read_back_and_the_yes_is_held_and_the_yes_creates()
    {
        var servers = Servers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.StartSession, Ask(root: "GodMode", action: "issue", issue: "283")).Respond("Ok.");
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");
        voice.Transcriptions.SayAsRecognized("Start issue 283 i GodMode");
        await voice.Events.SaidAsync("Skal jeg oprette issue 283 i GodMode, profil Godmode, som issue?");

        servers.Set(ServerA, Question("Godmode/GodMode/260930-issue-470-a1b2", "470-attention", "Hvilken farve?"));
        // Past the session's pause for announcements: held, it is not said
        await Task.Delay(VoiceBotSession.DefaultAnnouncementPause * 2);
        Assert.DoesNotContain(voice.Events.Responses, r => r.Contains("har et spørgsmål"));

        voice.Transcriptions.SayAsRecognized("Ja.");
        await voice.Events.SaidAsync("Opretter.");
        await Eventually.UntilAsync(() => voice.Events.Responses.Any(r => r.Contains("har et spørgsmål")), () => "the held announcement to be said");
        Assert.DoesNotContain("Der venter ingen oprettelse. Sig start igen.", voice.Events.Responses);
        var (_, action, inputs) = Assert.Single(servers.Creates);
        Assert.Equal(("issue", "283"), (action, inputs["issueNumber"]));
    }

    /// <summary>Held announcements are said once nothing waits: also when the read-back goes unanswered past its window.</summary>
    [Fact]
    public async Task What_is_held_is_released_when_the_wait_expires()
    {
        var time = new ManualTime();
        var tools = Tools(Servers(), time);
        var released = 0;
        tools.Creates.Released += () => released++;

        await Start(tools, Ask(root: "GodMode", action: "epic"));
        Assert.True(tools.Creates.Waiting);   // the draft's question
        tools.Creates.Settled();
        time.Advance(tools.Creates.DraftWindow + TimeSpan.FromSeconds(1));
        Assert.False(tools.Creates.Waiting);
        Assert.Equal(1, released);

        await Start(tools, Ask(root: "GodMode", action: "issue", issue: "283"));
        var request = tools.Creates.TakeProposed()!;
        var readBack = new VoicePhrases(new VoiceBot.Core.Resources.SessionLanguages("da-DK")).ReadBack(request);
        tools.Creates.ReadingBack(request, readBack);
        tools.Creates.Spoken(readBack);
        Assert.True(tools.Creates.Waiting);
        time.Advance(tools.Creates.ConfirmWindow + TimeSpan.FromSeconds(1));
        Assert.False(tools.Creates.Waiting);
        Assert.Equal(2, released);
    }

    /// <summary>Other speech between a read-back and its answer has the read-back said again after it: a create is never dropped silently.</summary>
    [Fact]
    public async Task Speech_between_the_read_back_and_its_answer_has_the_read_back_said_again()
    {
        var tools = Tools(Servers());
        List<string> repeated = [];
        tools.Creates.Repeat += repeated.Add;
        await Start(tools, Ask(root: "GodMode", action: "issue", issue: "283"));
        var request = tools.Creates.TakeProposed()!;
        const string readBack = "Skal jeg oprette issue 283 i GodMode, profil Godmode, som issue?";
        tools.Creates.ReadingBack(request, readBack);
        tools.Creates.Spoken(readBack);

        tools.Creates.Spoken("issue 470 har et spørgsmål.");
        Assert.Equal([readBack], repeated);
        Assert.Null(tools.Creates.Armed);
        tools.Creates.Spoken(readBack);
        Assert.NotNull(tools.Creates.Armed);
    }

    /// <summary>#471 in the log: an epic asked for as an issue. Its label makes it an epic, and the read-back says why.</summary>
    [Fact]
    public async Task An_epic_labelled_issue_asked_for_as_an_issue_is_read_back_as_an_epic()
    {
        var servers = Servers();
        servers.IssueLabels["471"] = ["epic"];
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.StartSession, Ask(root: "GodMode", action: "issue", issue: "471"));
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Start GodMode epic 471");
        await voice.Events.SaidAsync("Issue 471 er mærket epic. Skal jeg oprette issue 471 i GodMode, profil Godmode, som epic?");
        Assert.Contains("Issue 471 is labelled epic, so it is started as epic, not issue", Assert.Single(voice.ToolResults));
        Assert.DoesNotContain(voice.Events.Responses, r => r.Contains("som issue?"));

        voice.Transcriptions.SayAsRecognized("Ja");
        await voice.Events.SaidAsync("Opretter.");
        await voice.Events.SaidAsync("471 er oprettet.");
        Assert.Equal("epic", Assert.Single(servers.Creates).Action);
    }

    /// <summary>A root that cannot describe issues (no script, an older server) has its create read back as asked.</summary>
    [Fact]
    public async Task An_issue_no_root_describes_is_read_back_as_asked()
    {
        var servers = Servers();
        servers.IssueLabels["471"] = ["epic"];
        servers.DescribesIssues = false;
        var tools = Tools(servers);

        await Start(tools, Ask(root: "GodMode", action: "issue", issue: "471"));

        var request = tools.Creates.TakeProposed()!;
        Assert.Equal(("issue", null), (request.Action.Name, request.Label));
    }

    /// <summary>
    /// #471 in the log: "Launch a GodMode overseer for Epic 471" was refused with no tool call. The prompt names every
    /// session kind as started by the tool, and the actions voice does not start by name; the request reaches the tool.
    /// </summary>
    [Theory]
    [InlineData("Start an overseer for epic 471", "overseer", "epic 471", "Run epic 471.",
        "Skal jeg oprette epic 471 i GodMode, profil Godmode, som overseer, med beskrivelsen \"Run epic 471\"?")]
    [InlineData("Start an epic", "epic", null, null, null)]
    public async Task Starting_an_overseer_or_an_epic_calls_the_tool(string said, string action, string? name, string? prompt, string? readBack)
    {
        var servers = Servers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.StartSession, Ask(root: "GodMode", action: action, name: name, prompt: prompt)).Respond("Hvilket nummer?");
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized(said);
        await voice.Events.SaidAsync(readBack ?? "Hvilket nummer?");

        var system = model.Requests.Last().First(m => m.Role == Microsoft.Extensions.AI.ChatRole.System).Text;
        Assert.Contains("(the roots have issue, epic, overseer, chat)", system);
        Assert.Contains("Only the actions that start no session (new-root, promote) are not started by voice yet.", system);
        Assert.DoesNotContain("(new\n", system);
        Assert.Single(voice.ToolResults);
    }

    private static VoiceTools Tools(FakeServers servers, TimeProvider? time = null)
    {
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        return new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, new VoiceConversation(), time);
    }

    private static Task<string> Start(VoiceTools tools, Dictionary<string, object?> ask) =>
        tools.StartSessionAsync(new CreateAsk(Get(ask, VoiceTools.RootParameter), Get(ask, VoiceTools.ActionParameter), Get(ask, VoiceTools.IssueParameter),
            Get(ask, VoiceTools.NameParameter), Get(ask, VoiceTools.PromptParameter)), CancellationToken.None);

    private static string? Get(Dictionary<string, object?> ask, string key) => ask.GetValueOrDefault(key) as string;
}
