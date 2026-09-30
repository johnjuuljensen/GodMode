using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// Voice creates sessions (issue #354): an issue, or a chat with a name and a prompt, read back, and created only on
/// the user's yes. A root is never guessed, and a form voice cannot fill is declined with its reason.
/// </summary>
public sealed class CreateTests
{
    private const string ServerA = "server-a";
    private const string ReadBack = "Opret issue 283 i GodMode? Ja eller nej.";

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

    private static ScriptedChatClient StartIssue283(ScriptedChatClient? model = null) => (model ?? new ScriptedChatClient())
        .CallTool(VoiceTools.StartSession, new() { [VoiceTools.RootParameter] = "GodMode", [VoiceTools.IssueParameter] = "283" })
        .Respond(ReadBack);

    [Fact]
    public async Task Start_issue_is_read_back_and_created_on_yes_and_announced_by_its_handle()
    {
        var servers = Servers();
        var model = StartIssue283();
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Start issue 283 i GodMode");
        await voice.Events.SaidAsync(ReadBack);
        Assert.Equal("Read back and ask for a yes: create issue 283 (its title is not known here: read back the number) in " +
            "GodMode (profile Godmode), action issue. Nothing is created until the user says yes; anything else cancels it.",
            Assert.Single(model.ToolResults));
        Assert.Empty(servers.Creates);

        voice.Transcriptions.SayAsRecognized("Ja.");
        await voice.Events.SaidAsync("Opretter.");
        await voice.Events.SaidAsync("283 er oprettet.");

        var (root, action, inputs) = Assert.Single(servers.Creates);
        Assert.Equal(("GodMode", "Godmode", "issue"), (root.Root.Name, root.Profile, action));
        Assert.Equal(new Dictionary<string, string> { ["issueNumber"] = "283" }, inputs);
        // The yes is not the model's: it was called for the ask only
        Assert.Equal(2, model.Calls);
        // Known by its handle from then on, and what the conversation is about
        var created = new ProjectRef(ServerA, $"Godmode/GodMode/260930-issue-issue_283-0001");
        Assert.Equal(created, voice.Session.Handles.Resolve("283"));
    }

    [Theory]
    [InlineData("Nej.")]
    [InlineData("Nej tak")]
    [InlineData("Vent lidt")]
    [InlineData("Ja, men i kappe")]
    [InlineData("No")]
    public async Task Anything_but_a_clear_yes_cancels_and_creates_nothing(string answer)
    {
        var servers = Servers();
        var model = StartIssue283();
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Start issue 283 i GodMode");
        await voice.Events.SaidAsync(ReadBack);
        voice.Transcriptions.SayAsRecognized(answer);
        await voice.Events.SaidAsync("Annulleret. Intet oprettet.");

        // A yes after the cancel has nothing to confirm: it is the model's, and creates nothing either
        Assert.Null(voice.Session.Handles.Resolve("283"));
        Assert.Empty(servers.Creates);
        Assert.Equal(2, model.Calls);
    }

    /// <summary>The read-back is said and the user says nothing: nothing is created, however long it waits.</summary>
    [Fact]
    public async Task No_answer_creates_nothing()
    {
        var servers = Servers();
        var tools = Tools(servers, out _);

        Assert.StartsWith("Read back and ask for a yes", await Start(tools, root: "GodMode", issue: "283"));
        await Task.Delay(300);
        await tools.Creates.Running;

        Assert.NotNull(tools.Creates.Pending);
        Assert.Empty(servers.Creates);
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
            .Respond("Opret chatten backup job i Assistant? Ja eller nej.");
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Start en chat i assistenten om hvorfor backup-jobbet fejler");
        await voice.Events.SaidAsync("Opret chatten backup job i Assistant? Ja eller nej.");
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
        Assert.Equal(new Dictionary<string, string> { ["name"] = "backup job" }, tools.Creates.Pending!.Inputs);
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
        Assert.Null(tools.Creates.Pending);
        Assert.Null(tools.Creates.Confirm());
        await tools.Creates.Running;
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
        Assert.Contains("in Assistant (profile Private), action chat", await Start(tools, root: "Assistant Private", name: "backup job"));
        Assert.Equal("Private", tools.Creates.Pending!.Root.Profile);
    }

    [Fact]
    public async Task An_action_with_required_fields_voice_cannot_fill_is_declined_with_the_reason()
    {
        var tools = Tools(Servers(), out _);

        Assert.Equal("GodMode (profile Godmode), action branch needs Branch, which voice cannot fill: tell the user to create it in the app. Nothing was created.",
            await Start(tools, root: "GodMode", action: "branch", prompt: "Ret stavefejl."));
        Assert.Null(tools.Creates.Pending);
    }

    [Fact]
    public async Task A_required_field_voice_can_fill_but_was_not_said_is_asked_for()
    {
        var tools = Tools(Servers(), out _);

        Assert.Equal("experiments (profile Private), action experiment needs Task Description: ask the user for it. Nothing was created yet.",
            await Start(tools, action: "eksperiment", name: "sorting"));
        Assert.Null(tools.Creates.Pending);
    }

    /// <summary>An issue said as a key is a Jira case: only the root whose issue takes one fits, and none that takes a number.</summary>
    [Fact]
    public async Task A_Jira_key_fits_only_the_root_that_takes_keys()
    {
        var tools = Tools(Servers(), out _);

        Assert.Contains("create issue BD-123 (its title is not known here: read back the number) in api_worktrees (profile Mega), action issue",
            await Start(tools, issue: "bd 123"));
        Assert.Equal(new Dictionary<string, string> { ["issueKey"] = "BD-123" }, tools.Creates.Pending!.Inputs);
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

    /// <summary>After the create, the new session is what the conversation is about: "status" with no project is its status.</summary>
    [Fact]
    public async Task The_new_session_is_the_one_talked_about()
    {
        var servers = Servers();
        var tools = Tools(servers, out var handles);
        CreateOutcome? outcome = null;
        tools.Creates.Attach(o => outcome = o);

        await Start(tools, root: "kappe", issue: "41");
        Assert.NotNull(tools.Creates.Confirm());
        await tools.Creates.Running;

        Assert.Equal("41", outcome!.Handle);
        Assert.Equal(outcome.Project, handles.Resolve("41"));
        Assert.StartsWith("41 (issue_41, kappe, issue): Idle.", await tools.ProjectStatusAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_create_is_announced_with_its_reason()
    {
        var servers = Servers();
        servers.CreateError = "Issue #999 was not found";
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.StartSession, new() { [VoiceTools.RootParameter] = "GodMode", [VoiceTools.IssueParameter] = "999" })
            .Respond("Opret issue 999 i GodMode?");
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Start issue 999 i GodMode");
        await voice.Events.SaidAsync("Opret issue 999 i GodMode?");
        voice.Transcriptions.SayAsRecognized("Ja");
        await voice.Events.SaidAsync("Oprettelsen i GodMode fejlede: Issue #999 was not found.");
    }

    private static VoiceTools Tools(FakeServers servers, out ProjectHandles handles)
    {
        handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        return new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, new VoiceConversation());
    }

    private static Task<string> Start(VoiceTools tools, string? root = null, string? action = null, string? issue = null, string? name = null, string? prompt = null) =>
        tools.StartSessionAsync(new CreateAsk(root, action, issue, name, prompt), CancellationToken.None);
}
