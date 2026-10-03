using GodMode.Shared.Models;
using VoiceBot.Core.Graph;
using VoiceBot.Core.Resources;
using VoiceBot.Core.Speech;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// Voice creates sessions (issue #354): an issue, or a chat with a name and a prompt, read back in the code's fixed
/// words, and created only on the user's yes to that read-back, said after it started playing. A root is never
/// guessed, and a form voice cannot fill is declined with its reason.
/// </summary>
public sealed class CreateTests
{
    private const string ServerA = "server-a";
    private const string ReadBack283 = "Skal jeg oprette issue 283 i GodMode, profil Godmode, som issue?";
    private static readonly VoicePhrases Danish = new(new SessionLanguages("da-DK"));

    /// <summary>The user's roots, as the main server lists them: repos with issues, chats, experiments, and provisioning.</summary>
    private static FakeServers Servers() => new FakeServers(ServerA)
        .AddRoot(ServerA, "GodMode", "Godmode", Action("branch", BranchSchema), Action("issue", IssueSchema))
        .AddRoot(ServerA, "kappe", "Kappe", Action("branch", BranchSchema), Action("issue", IssueSchema))
        .AddRoot(ServerA, "api_worktrees", "Mega", Action("issue", """
            { "type": "object", "properties": { "issueKey": { "type": "string", "title": "Jira Case" } }, "required": ["issueKey"] }
            """))
        .AddRoot(ServerA, "Assistant", "Outbound", Action("chat"))
        .AddRoot(ServerA, "experiments", "Private", Action("experiment", """
            { "type": "object", "properties": { "name": { "type": "string", "title": "Name" },
              "prompt": { "type": "string", "title": "Task Description" } }, "required": ["name", "prompt"] }
            """))
        .AddRoot(ServerA, "provisioning", "Private", Action("new-root", session: false), Action("promote", session: false));

    /// <summary>The model settles "start issue 283 in GodMode", and replies with a word the read-back replaces.</summary>
    private static ScriptedChatClient StartIssue283(ScriptedChatClient? model = null) => (model ?? new ScriptedChatClient())
        .CallTool(VoiceTools.StartSession, new() { [VoiceTools.RootParameter] = "GodMode", [VoiceTools.IssueParameter] = "283" })
        .Respond("Oprettet.");

    private static async Task<OfflineVoice> ReadBack283Async(FakeServers servers, ScriptedChatClient model)
    {
        var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");
        voice.Transcriptions.SayAsRecognized("Start issue 283 i GodMode");
        await voice.Events.SaidAsync(ReadBack283);
        return voice;
    }

    [Fact]
    public async Task Start_issue_is_read_back_in_fixed_words_and_created_on_yes_and_announced_by_its_handle()
    {
        var servers = Servers();
        var model = StartIssue283();
        await using var voice = await ReadBack283Async(servers, model);
        Assert.StartsWith("Settled: create issue 283 (its title is not known here: read back the number) in GodMode (profile Godmode), action issue.",
            Assert.Single(model.ToolResults));
        // The model's own words are never said: the read-back is said in their place
        Assert.DoesNotContain("Oprettet.", voice.Events.Responses);
        Assert.Empty(servers.Creates);

        voice.Transcriptions.SayAsRecognized("Ja.");
        await voice.Events.SaidAsync("Opretter.");
        await voice.Events.SaidAsync("283 er oprettet.");

        var (root, action, inputs) = Assert.Single(servers.Creates);
        Assert.Equal(("GodMode", "Godmode", "issue"), (root.Root.Name, root.Profile, action));
        Assert.Equal(new Dictionary<string, string> { ["issueNumber"] = "283" }, inputs);
        // The yes is not the model's: it was called for the ask only
        Assert.Equal(2, model.Calls);
        Assert.Equal(new ProjectRef(ServerA, "Godmode/GodMode/260930-issue-issue_283-0001"), voice.Session.Handles.Resolve("283"));
    }

    [Theory]
    [InlineData("Nej.")]
    [InlineData("Nej tak")]
    [InlineData("Vent lidt")]
    [InlineData("No")]
    [InlineData("Okay.")]
    [InlineData("OK")]
    public async Task Anything_but_a_clear_yes_cancels_and_creates_nothing(string answer)
    {
        var servers = Servers();
        var model = StartIssue283();
        await using var voice = await ReadBack283Async(servers, model);

        voice.Transcriptions.SayAsRecognized(answer);
        await voice.Events.SaidAsync("Annulleret. Intet oprettet.");

        Assert.Empty(servers.Creates);
        Assert.Equal(2, model.Calls);
    }

    /// <summary>
    /// A partial "ja" the final goes on from ("ja, men i kappe") is no yes: the final decides. It is an answer with a
    /// change (#449): it goes to the chat, which proposes the create again as changed, and that is read back.
    /// </summary>
    [Fact]
    public async Task A_partial_yes_revised_by_its_final_into_a_change_is_proposed_again()
    {
        var servers = Servers();
        var model = StartIssue283()
            .CallTool(VoiceTools.StartSession, new() { [VoiceTools.RootParameter] = "kappe", [VoiceTools.IssueParameter] = "283" })
            .Respond("Ok.");
        await using var voice = await ReadBack283Async(servers, model);

        voice.Transcriptions.AddPartial("Ja");
        voice.Transcriptions.AddFinal("Ja, men i kappe");
        await voice.Events.SaidAsync("Skal jeg oprette issue 283 i kappe, profil Kappe, som issue?");

        Assert.Empty(servers.Creates);
        Assert.DoesNotContain("Annulleret. Intet oprettet.", voice.Events.Responses);
    }

    /// <summary>
    /// The voice log of 2026-10-03 (#449): an overseer asked for, read back as a chat, and corrected at the read-back
    /// ("Nej, som overseer", heard "Now as overseer"). The correction is no cancel: it reaches the chat with the create
    /// it answers, the model proposes it again with the action changed, and the user's yes to that creates it. (Not
    /// "Nej, som overseer." here: VoiceBot's echo filter takes three words, two of them the read-back's, for its echo.)
    /// </summary>
    [Theory]
    [InlineData("Nej, lav den som overseer i stedet.")]
    [InlineData("Now as overseer.")]
    public async Task A_no_with_a_change_at_the_read_back_reaches_the_chat_and_is_read_back_again(string correction)
    {
        var servers = new FakeServers(ServerA).AddRoot(ServerA, "GodMode", "Godmode", Action("chat"), Action("overseer"));
        static Dictionary<string, object?> Ask(string action) => new()
        {
            [VoiceTools.RootParameter] = "GodMode",
            [VoiceTools.ActionParameter] = action,
            [VoiceTools.NameParameter] = "overseer",
            [VoiceTools.PromptParameter] = "Triage the issues.",
        };
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.StartSession, Ask("chat")).Respond("Ok.")
            .CallTool(VoiceTools.StartSession, Ask("overseer")).Respond("Ok.");
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Start en overseer i GodMode der skal triage issues");
        await voice.Events.SaidAsync("Skal jeg oprette overseer i GodMode, profil Godmode, som chat, med beskrivelsen \"Triage the issues\"?");
        voice.Transcriptions.SayAsRecognized(correction);
        await voice.Events.SaidAsync("Skal jeg oprette overseer i GodMode, profil Godmode, som overseer, med beskrivelsen \"Triage the issues\"?");

        Assert.DoesNotContain("Annulleret. Intet oprettet.", voice.Events.Responses);
        Assert.Empty(servers.Creates);
        // The model heard the user's words with the create they answer
        var heard = model.Requests.ElementAt(2)[^1].Text;
        Assert.Contains(correction, heard);
        Assert.Contains("answer the read-back", heard);
        Assert.Contains("the prompt 'Triage the issues.' in GodMode (profile Godmode), action chat", heard);

        voice.Transcriptions.SayAsRecognized("Ja");
        await voice.Events.SaidAsync("Opretter.");
        await voice.Events.SaidAsync("overseer er oprettet.");
        var (root, action, inputs) = Assert.Single(servers.Creates);
        Assert.Equal(("GodMode", "overseer"), (root.Root.Name, action));
        Assert.Equal(new Dictionary<string, string> { ["name"] = "overseer", ["prompt"] = "Triage the issues." }, inputs);
    }

    /// <summary>A change at the read-back drops the create it answers: it waits no more, and only the chat hears of it, once.</summary>
    [Fact]
    public async Task A_change_at_the_read_back_drops_the_create_it_answers()
    {
        var servers = Servers();
        var clock = new ManualClock();
        var tools = Tools(servers, out _, clock);
        var node = new ConfirmCreateNode("confirm-create", 70, tools.Creates, Danish);
        await Start(tools, root: "GodMode", issue: "283");
        Arm(tools);
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Null(await node.EvaluateAsync(Final("Nej, i kappe", clock.GetUtcNow()), CancellationToken.None));
        Assert.Null(tools.Creates.Armed);
        Assert.Contains("issue 283", tools.Creates.TakeCorrected()!.Note);
        Assert.Null(tools.Creates.TakeCorrected());
        await tools.Creates.Running;
        Assert.Empty(servers.Creates);
    }

    [Theory]
    [InlineData("Nej, som overseer", true)]
    [InlineData("Ja, men i kappe", true)]
    [InlineData("Nej", false)]
    [InlineData("Nej tak.", false)]
    [InlineData("Okay", false)]
    [InlineData("Ja", false)]
    [InlineData("i GodMode, profil Godmode", false)]   // its echo
    [InlineData("", false)]
    public void A_change_is_anything_but_a_yes_a_plain_no_or_the_read_backs_echo(string said, bool change) =>
        Assert.Equal(change, ConfirmCreateNode.IsChangeTo(said, ReadBack283));

    /// <summary>
    /// VoiceBot#61: a final carries the partials it revised, for the model. The yes is the final's own words: an
    /// earlier reading "ja" of a final that says something else ("Kører gør man.") is no yes, through the session and
    /// at the node. It goes to the chat, with the create it answers, as any answer but a yes or a plain no (#449).
    /// </summary>
    [Fact]
    public async Task An_earlier_reading_yes_of_a_final_that_is_no_yes_does_not_create()
    {
        var servers = Servers();
        await using var voice = await ReadBack283Async(servers, StartIssue283().Respond("Annulleret."));

        voice.Transcriptions.AddPartial("Ja.");
        voice.Transcriptions.AddFinal("Kører gør man.");
        await voice.Events.SaidAsync("Annulleret.");

        Assert.Empty(servers.Creates);
    }

    [Fact]
    public async Task A_final_that_is_no_yes_does_not_create_whatever_its_earlier_readings()
    {
        var servers = Servers();
        var clock = new ManualClock();
        var tools = Tools(servers, out _, clock);
        var node = new ConfirmCreateNode("confirm-create", 70, tools.Creates, Danish);
        await Start(tools, root: "GodMode", issue: "283");
        Arm(tools);
        clock.Advance(TimeSpan.FromSeconds(2));

        var context = Final("Kører gør man.", clock.GetUtcNow());
        context.LatestTranscription = context.LatestTranscription! with { Readings = ["Ja.", "ja tak"] };

        Assert.Null(await node.EvaluateAsync(context, CancellationToken.None));
        Assert.Null(tools.Creates.Armed);
        await tools.Creates.Running;
        Assert.Empty(servers.Creates);
    }

    /// <summary>
    /// The review's case: "Start issue 283", and a "ja" said while the model still works on it, before the read-back
    /// is said. It is evaluated after the read-back started, and barges in on it, but was said before: it is no answer to it.
    /// </summary>
    [Fact]
    public async Task A_yes_said_before_the_read_back_does_not_create()
    {
        var servers = Servers();
        var roots = new TaskCompletionSource();
        servers.RootsGate = roots.Task;
        await using var voice = await OfflineVoice.StartAsync(servers, StartIssue283());
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Start issue 283 i GodMode");
        await Eventually.UntilAsync(() => servers.RootsListed == 1, () => "the tool to ask for the roots");
        voice.Transcriptions.SayAsRecognized("Ja.");
        roots.SetResult();
        await voice.Events.SaidAsync(ReadBack283);
        await voice.Events.SaidAsync("Annulleret. Intet oprettet.");

        Assert.Empty(servers.Creates);
    }

    /// <summary>
    /// The review's case: an announcement is said after the read-back, and the "ja" that follows answers that, not
    /// the read-back: the create was dropped when the bot said something else, and the yes is told so.
    /// </summary>
    [Fact]
    public async Task An_announcement_after_the_read_back_drops_the_create()
    {
        var servers = Servers();
        await using var voice = await ReadBack283Async(servers, StartIssue283());

        servers.Set(ServerA, Permission("Kappe/kappe/260930-issue-12-a1b2", "12-deploy", "Bash: git push"));
        await voice.Events.SaidAsync("issue 12 skal have tilladelse: Bash: git push. Svar på skærmen.");
        voice.Transcriptions.SayAsRecognized("Ja.");
        await voice.Events.SaidAsync("Der venter ingen oprettelse. Sig start igen.");

        Assert.Empty(servers.Creates);
    }

    /// <summary>The read-back is said and the user says nothing: nothing is created, and after the window a yes finds nothing to confirm.</summary>
    [Fact]
    public async Task No_answer_creates_nothing_and_a_late_yes_is_told_nothing_waits()
    {
        var servers = Servers();
        var clock = new ManualClock();
        var tools = Tools(servers, out _, clock);
        var node = new ConfirmCreateNode("confirm-create", 70, tools.Creates, Danish);
        await Start(tools, root: "GodMode", issue: "283");
        var readBack = Arm(tools);
        Assert.Equal(ReadBack283, readBack);

        clock.Advance(TimeSpan.FromSeconds(19));
        Assert.NotNull(tools.Creates.Armed);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(tools.Creates.Armed);

        var answered = await node.EvaluateAsync(Final("Ja", clock.GetUtcNow()), CancellationToken.None);
        Assert.Equal("Der venter ingen oprettelse. Sig start igen.", answered?.ResponseText);
        await tools.Creates.Running;
        Assert.Empty(servers.Creates);
        // Said once: the next yes is the model's
        Assert.Null(await node.EvaluateAsync(Final("Ja", clock.GetUtcNow()), CancellationToken.None));
    }

    /// <summary>A yes within the window, said after the read-back started, creates: the node's own decision, on a clock.</summary>
    [Fact]
    public async Task A_yes_within_the_window_creates()
    {
        var servers = Servers();
        var clock = new ManualClock();
        var tools = Tools(servers, out _, clock);
        var node = new ConfirmCreateNode("confirm-create", 70, tools.Creates, Danish);
        await Start(tools, root: "GodMode", issue: "283");
        Arm(tools);
        clock.Advance(TimeSpan.FromSeconds(8));

        Assert.Equal("Opretter.", (await node.EvaluateAsync(Final("ja tak", clock.GetUtcNow()), CancellationToken.None))?.ResponseText);
        await tools.Creates.Running;
        Assert.Single(servers.Creates);
    }

    /// <summary>
    /// The review's case: a read-back holding "ja" would be confirmed by its own echo "Ja.". The fixed read-backs hold
    /// no yes, in either language and for every shape; and a yes that is a fragment of the read-back is refused anyway.
    /// </summary>
    [Fact]
    public void The_read_back_holds_no_yes_and_its_echo_is_no_yes()
    {
        var root = new ServerRoot(ServerA, "main", new ProjectRootInfo("GodMode", null, [], "Godmode"));
        var action = new CreateActionInfo("issue");
        CreateRequest[] requests =
        [
            new(root, action, new Dictionary<string, string>(), "", Issue: "283"),
            new(root, action, new Dictionary<string, string>(), "", Name: "backup job", WithPrompt: true, SeveralServers: true),
            new(root, action, new Dictionary<string, string>(), "", Name: "backup job", WithPrompt: false),
            new(root, action, new Dictionary<string, string>(), "", Name: "backup job"),
            new(root, action, new Dictionary<string, string>(), ""),
            new(root, action, new Dictionary<string, string>(), "", Name: "backup job", WithPrompt: true, Prompt: "Find ud af hvorfor backup-jobbet fejler."),
            new(root, action, new Dictionary<string, string>(), "", Name: "doer", WithPrompt: true, Prompt: "Say yes to it, ja tak."),
        ];
        foreach (var phrases in new[] { Danish, new VoicePhrases(new SessionLanguages("en-US")) })
            Assert.All(requests, r => Assert.False(ConfirmCreateNode.HoldsYes(phrases.ReadBack(r)), phrases.ReadBack(r)));

        Assert.True(ConfirmCreateNode.HoldsYes("Opret issue 283 i GodMode? Ja eller nej."));
        Assert.False(ConfirmCreateNode.IsYesTo("Ja.", "Opret issue 283 i GodMode? Ja eller nej."));
        Assert.True(ConfirmCreateNode.IsYesTo("Ja.", ReadBack283));
    }

    /// <summary>
    /// The review's case: a slow create finishes while another project is talked about. Its announcement names it,
    /// but an answer with no project still goes to the one talked about.
    /// </summary>
    [Fact]
    public async Task A_create_finishing_mid_conversation_does_not_take_the_next_answer()
    {
        var servers = Servers();
        var create = new TaskCompletionSource();
        servers.CreateGate = create.Task;
        var model = StartIssue283(new ScriptedChatClient()
                .CallTool(VoiceTools.ProjectStatus, new() { [VoiceTools.ProjectParameter] = "101" }).Respond("issue 101 spørger om kolonnerne."))
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.TextParameter] = "Slet dem." }).Respond("Sendt til 101.");
        await using var voice = await OfflineVoice.StartAsync(servers, model,
            connect: _ => { servers.Set(ServerA, Question("Kappe/kappe/260930-issue-101-c3", "101-cleanup", "Slet kolonnerne?")); return Task.CompletedTask; });
        await voice.Events.SaidAsync("issue 101 har et spørgsmål.");

        voice.Transcriptions.SayAsRecognized("Status 101");
        await voice.Events.SaidAsync("issue 101 spørger om kolonnerne.");
        voice.Transcriptions.SayAsRecognized("Start issue 283 i GodMode");
        await voice.Events.SaidAsync(ReadBack283);
        voice.Transcriptions.SayAsRecognized("Ja");
        await voice.Events.SaidAsync("Opretter.");
        create.SetResult();
        await voice.Events.SaidAsync("283 er oprettet.");

        voice.Transcriptions.SayAsRecognized("Svar at den skal slette dem");
        await voice.Events.SaidAsync("Sendt til issue 101 i root.");
        Assert.Equal(new ProjectRef(ServerA, "Kappe/kappe/260930-issue-101-c3"), Assert.Single(servers.Replies).Project);
    }

    /// <summary>The same issue confirmed again while its create still runs is one create.</summary>
    [Fact]
    public async Task A_create_in_flight_is_not_proposed_again()
    {
        var servers = Servers();
        var create = new TaskCompletionSource();
        servers.CreateGate = create.Task;
        var tools = Tools(servers, out _);
        await Start(tools, root: "GodMode", issue: "283");
        Arm(tools);
        Assert.NotNull(tools.Creates.Confirm(tools.Creates.Armed!));

        Assert.StartsWith("issue 283 (its title is not known here: read back the number) in GodMode (profile Godmode), action issue is being created already",
            await Start(tools, root: "GodMode", issue: "283"));
        Assert.Null(tools.Creates.TakeProposed());
        create.SetResult();
        await tools.Creates.Running;
        Assert.Single(servers.Creates);
    }

    [Fact]
    public async Task A_chat_is_created_with_a_name_and_a_prompt()
    {
        var servers = Servers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.StartSession, new()
            {
                [VoiceTools.RootParameter] = "assistenten",
                [VoiceTools.ActionParameter] = "chat",
                [VoiceTools.NameParameter] = "backup job",
                [VoiceTools.PromptParameter] = "Find ud af hvorfor backup-jobbet fejler.",
            })
            .Respond("Ok.");
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Start en chat i assistenten om hvorfor backup-jobbet fejler");
        await voice.Events.SaidAsync("Skal jeg oprette backup job i Assistant, profil Outbound, som chat, med beskrivelsen \"Find ud af hvorfor backup-jobbet fejler\"?");
        Assert.Contains("create a session named 'backup job', the prompt 'Find ud af hvorfor backup-jobbet fejler.' in Assistant (profile Outbound), action chat",
            Assert.Single(model.ToolResults));

        voice.Transcriptions.SayAsRecognized("Ja tak");
        await voice.Events.SaidAsync("backup er oprettet.");

        var (root, action, inputs) = Assert.Single(servers.Creates);
        Assert.Equal(("Assistant", "chat"), (root.Root.Name, action));
        Assert.Equal(new Dictionary<string, string> { ["name"] = "backup job", ["prompt"] = "Find ud af hvorfor backup-jobbet fejler." }, inputs);
    }

    /// <summary>With #352 a chat needs no prompt: it starts idle, and the read-back says so.</summary>
    [Fact]
    public async Task A_chat_without_a_prompt_is_read_back_as_starting_idle()
    {
        var tools = Tools(Servers(), out _);

        var said = await Start(tools, root: "Assistant", name: "backup job");

        Assert.Contains("a session named 'backup job', no prompt (it starts idle, waiting for the first message) in Assistant", said);
        var request = tools.Creates.TakeProposed()!;
        Assert.Equal(new Dictionary<string, string> { ["name"] = "backup job" }, request.Inputs);
        Assert.Equal("Skal jeg oprette backup job uden beskrivelse i Assistant, profil Outbound, som chat?", Danish.ReadBack(request));
    }

    [Theory]
    [InlineData(null, "283", "GodMode (profile Godmode), kappe (profile Kappe)")]
    [InlineData(null, "to hundrede og treogfirs", "GodMode (profile Godmode), kappe (profile Kappe)")]
    [InlineData(null, null, "Assistant (profile Outbound), experiments (profile Private)")]
    public async Task An_ambiguous_root_is_asked_about_and_nothing_waits_on_a_yes(string? root, string? issue, string options)
    {
        var servers = Servers();
        var tools = Tools(servers, out _);

        var said = await Start(tools, root: root, issue: issue, name: issue is null ? "backup job" : null, prompt: issue is null ? "Tjek backup." : null);

        Assert.Equal($"Ambiguous: 2 roots fit. Ask the user which, as a closed question: {options}. Nothing was created.", said);
        Assert.Null(tools.Creates.TakeProposed());
        Assert.Null(tools.Creates.Armed);
        Assert.Empty(servers.Creates);
    }

    /// <summary>The same root name in two profiles, as the user's work and private Assistants: the profile is asked for.</summary>
    [Fact]
    public async Task A_root_in_two_profiles_is_asked_about_and_the_profile_named_settles_it()
    {
        var servers = Servers().AddRoot(ServerA, "Assistant", "Private", Action("chat"));
        var tools = Tools(servers, out _);

        Assert.Equal("Ambiguous: 2 roots fit. Ask the user which, as a closed question: Assistant (profile Outbound), Assistant (profile Private). Nothing was created.",
            await Start(tools, root: "Assistant", name: "backup job"));
        Assert.Null(tools.Creates.TakeProposed());
        Assert.Contains("in Assistant (profile Private), action chat", await Start(tools, root: "Assistant Private", name: "backup job"));
        Assert.Equal("Private", tools.Creates.TakeProposed()!.Root.Profile);
    }

    [Fact]
    public async Task An_action_with_required_fields_voice_cannot_fill_is_declined_with_the_reason()
    {
        var tools = Tools(Servers(), out _);

        Assert.Equal("GodMode (profile Godmode), action branch needs Branch, which voice cannot fill: tell the user to create it in the app. Nothing was created.",
            await Start(tools, root: "GodMode", action: "branch", prompt: "Ret stavefejl."));
        Assert.Null(tools.Creates.TakeProposed());
    }

    [Fact]
    public async Task A_required_field_voice_can_fill_but_was_not_said_is_asked_for()
    {
        var tools = Tools(Servers(), out _);

        Assert.Equal("experiments (profile Private), action experiment needs Task Description: ask the user for it, then call start_session " +
            "again with action 'experiment', the same root, and their answer. Keep that action: never start another in its place, nor put " +
            "its name in another's name. Nothing was created yet.",
            await Start(tools, action: "eksperiment", name: "sorting"));
        Assert.Null(tools.Creates.TakeProposed());
    }

    /// <summary>An issue said as a key is a Jira case: only the root whose issue takes one fits, and none that takes a number.</summary>
    [Fact]
    public async Task A_Jira_key_fits_only_the_root_that_takes_keys()
    {
        var tools = Tools(Servers(), out _);

        Assert.Contains("create issue BD-123 (its title is not known here: read back the number) in api_worktrees (profile Mega), action issue",
            await Start(tools, issue: "bd 123"));
        Assert.Equal(new Dictionary<string, string> { ["issueKey"] = "BD-123" }, tools.Creates.TakeProposed()!.Inputs);
        Assert.StartsWith("No action there takes the issue 'BD-123'", await Start(tools, root: "GodMode", issue: "BD-123"));
    }

    [Fact]
    public async Task Actions_that_start_no_session_are_left_out()
    {
        var tools = Tools(Servers(), out _);

        Assert.StartsWith("The root 'provisioning' has only actions that start no session", await Start(tools, root: "provisioning", name: "new"));
        Assert.StartsWith("Unknown root 'vonage'. Nothing was created. Roots: GodMode (profile Godmode), kappe (profile Kappe), api_worktrees",
            await Start(tools, root: "vonage", issue: "283"));
    }

    /// <summary>After the create, the new session is known by its handle, and what the conversation is about stays as it was.</summary>
    [Fact]
    public async Task The_new_session_is_known_by_its_handle_and_the_conversation_stays()
    {
        var servers = Servers();
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var conversation = new VoiceConversation();
        var tools = new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, conversation);
        CreateOutcome? outcome = null;
        tools.Creates.Attach(o => outcome = o);

        await Start(tools, root: "kappe", issue: "41");
        Arm(tools);
        Assert.NotNull(tools.Creates.Confirm(tools.Creates.Armed!));
        await tools.Creates.Running;

        Assert.Equal("41", outcome!.Handle);
        Assert.Equal(outcome.Project, handles.Resolve("41"));
        Assert.Null(conversation.Current);
        Assert.StartsWith("issue 41 (issue_41, issue): Idle.", await tools.ProjectStatusAsync("41", CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_create_is_announced_with_its_reason()
    {
        var servers = Servers();
        servers.CreateError = "Issue #999 was not found";
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.StartSession, new() { [VoiceTools.RootParameter] = "GodMode", [VoiceTools.IssueParameter] = "999" })
            .Respond("Ok.");
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Start issue 999 i GodMode");
        await voice.Events.SaidAsync("Skal jeg oprette issue 999 i GodMode, profil Godmode, som issue?");
        voice.Transcriptions.SayAsRecognized("Ja");
        await voice.Events.SaidAsync("Kunne ikke oprette issue 999 i Godmode / GodMode: Issue #999 was not found.");
    }

    /// <summary>
    /// The voice log of 2026-10-03 (#449): a create script's failure was read out whole, paths and all, for 9.5 seconds.
    /// The announcement says what failed and where, and that the log has the rest.
    /// </summary>
    [Fact]
    public async Task A_create_scripts_failure_is_announced_short_by_its_profile_root_and_action()
    {
        var servers = Servers();
        servers.CreateError = @"Script '.godmode-root\chat/create' exited with code 1: fatal: not a git repository: ../../../.bare/worktrees/chat/modules/external/VoiceBot";
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.StartSession, new() { [VoiceTools.RootParameter] = "Assistant", [VoiceTools.NameParameter] = "backup job" })
            .Respond("Ok.");
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Start en chat i Assistant om backup-jobbet");
        await voice.Events.SaidAsync("Skal jeg oprette backup job uden beskrivelse i Assistant, profil Outbound, som chat?");
        voice.Transcriptions.SayAsRecognized("Ja");
        await voice.Events.SaidAsync("Kunne ikke oprette chat i Outbound / Assistant: create-scriptet fejlede. Loggen har resten.");
    }

    [Fact]
    public void A_failure_is_said_in_English_short_and_a_long_error_not_at_all()
    {
        var english = new VoicePhrases(new SessionLanguages("en-US"));
        var request = new CreateRequest(new ServerRoot(ServerA, "main", new ProjectRootInfo("GodMode", null, [], "Godmode")),
            new CreateActionInfo("chat"), new Dictionary<string, string>(), "", Name: "overseer");

        Assert.Equal("Could not create the chat in Godmode / GodMode: its create script failed. The log has the details",
            english.Failed(request, @"Script '.godmode-root\chat/create' exited with code 1: fatal: not a git repository: ../../../.bare"));
        Assert.Equal("Could not create the chat in Godmode / GodMode. The log has the details", english.Failed(request, new string('x', 200)));
        Assert.Equal("Could not create the chat in Godmode / GodMode. The log has the details", english.Failed(request, @"Could not find C:\repos\GodMode"));
        Assert.Equal("Could not create issue 7 in Godmode / GodMode: Issue #7 was not found",
            english.Failed(request with { Issue = "7" }, "Issue #7 was not found."));
    }

    /// <summary>
    /// The voice log of 2026-10-03 (#449): "Klar." in an English session. The words the control prompt gives the model
    /// for itself are the session's language's, and so are the bot's own.
    /// </summary>
    [Fact]
    public async Task An_English_session_gives_the_model_English_protocol_words()
    {
        var model = new ScriptedChatClient().Respond("Unknown.");
        await using var voice = await OfflineVoice.StartAsync(Servers(), model, settings: VoiceSettings.Default with { Language = "en-US" });
        await voice.Events.SaidAsync("Ready.");

        voice.Transcriptions.SayAsRecognized("Status of 999");
        await voice.Events.SaidAsync("Unknown.");

        var prompt = model.Requests.Last().First(m => m.Role == Microsoft.Extensions.AI.ChatRole.System).Text;
        Assert.Contains("\"Ready\" (ready), \"Unknown\" (no such project), \"Unclear\"", prompt);
        Assert.Contains("<name> needs permission: <what>. Answer it on screen.", prompt);
        Assert.DoesNotContain("Klar", prompt);
        Assert.DoesNotContain("Uklar", prompt);
    }

    /// <summary>The read-back says a short prompt as it is (#449), cuts a long one after its first words, and never says one holding a yes.</summary>
    [Fact]
    public void The_read_back_holds_the_prompt()
    {
        var english = new VoicePhrases(new SessionLanguages("en-US"));
        var request = new CreateRequest(new ServerRoot(ServerA, "main", new ProjectRootInfo("GodMode", null, [], "Godmode")),
            new CreateActionInfo("chat"), new Dictionary<string, string>(), "", Name: "overseer", WithPrompt: true, Prompt: "Triage the issues.");

        Assert.Equal("Shall I create overseer in GodMode, profile Godmode, as chat, with the prompt \"Triage the issues\"?", english.ReadBack(request));
        Assert.Equal("Skal jeg oprette overseer i GodMode, profil Godmode, som chat, med beskrivelsen \"Triage the issues\"?", Danish.ReadBack(request));

        var cut = english.ReadBack(request with { Prompt = string.Join(" ", Enumerable.Repeat("Go through every open issue and sort it by area.", 5)) });
        Assert.StartsWith("Shall I create overseer in GodMode, profile Godmode, as chat, with the prompt \"Go through every open issue", cut);
        Assert.EndsWith(" …\"?", cut);
        Assert.True(cut.Length < 200, cut);

        Assert.Equal("Shall I create overseer with a description in GodMode, profile Godmode, as chat?",
            english.ReadBack(request with { Prompt = "Answer yes to everything." }));
    }

    private static VoiceTools Tools(FakeServers servers, out ProjectHandles handles, TimeProvider? time = null)
    {
        handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        return new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, new VoiceConversation(), time);
    }

    private static Task<string> Start(VoiceTools tools, string? root = null, string? action = null, string? issue = null, string? name = null, string? prompt = null) =>
        tools.StartSessionAsync(new CreateAsk(root, action, issue, name, prompt), CancellationToken.None);

    /// <summary>What the graph does with a create settled on: its read-back is said, and starts playing (ReadBackNode, the event sink).</summary>
    private static string Arm(VoiceTools tools)
    {
        var request = tools.Creates.TakeProposed()!;
        var readBack = Danish.ReadBack(request);
        tools.Creates.ReadingBack(request, readBack);
        tools.Creates.Spoken(readBack);
        Assert.NotNull(tools.Creates.Armed);
        return readBack;
    }

    private static NodeContext Final(string text, DateTimeOffset at) => new()
    {
        LatestTranscription = new TranscriptionEvent { Text = text, IsPartial = false, Timestamp = at },
        StateKey = "root.confirm-create",
    };

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
