namespace GodMode.Voice.Tests;

/// <summary>
/// Voice knows every project the servers have, for as long as it runs: one created after it started (from the app),
/// one that has never needed the user, and one with no issue number (a chat in a root that is its own workspace),
/// and forgets one that is deleted (issue #353).
/// </summary>
public sealed class ProjectListTests
{
    private const string ServerA = "server-a";
    private const string Chat = "Outbound/Assistant/260930-chat-testing-lgp2";

    /// <summary>
    /// As the user reported it: an Assistant chat created from the app after voice started, that needs nothing, is
    /// asked for as the projects that run, and answered by its root, as speech recognition wrote it ("Assistent").
    /// </summary>
    [Fact]
    public async Task A_session_created_after_voice_started_is_listed_and_answered_by_its_root()
    {
        // The server is there when voice starts, with no projects yet
        var servers = new FakeServers(ServerA);
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ListProjects).Respond("1 projekt: testing.")
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.ProjectParameter] = "Assistent", [VoiceTools.TextParameter] = "Skift til outbound profil." })
            .Respond("Sendt til testing.");
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");

        servers.AddProject(ServerA, Chat, "testing", root: "Assistant", kind: "chat", profile: "Outbound");

        voice.Transcriptions.AddFinal("Hvilke projekter er i gang?");
        await voice.Events.SaidAsync("1 projekt: testing.");
        Assert.Equal("1 projects:\n- testing (testing, Assistant, Outbound, chat): Idle", Assert.Single(model.ToolResults));

        voice.Transcriptions.AddFinal("Sig til Assistent at den skal skifte til outbound profil");
        await voice.Events.SaidAsync("Sendt til testing.");

        var (project, text) = Assert.Single(servers.Replies);
        Assert.Equal(new ProjectRef(ServerA, Chat), project);
        Assert.Equal("Skift til outbound profil.", text);
    }

    [Fact]
    public async Task A_deleted_session_is_forgotten_and_its_handle_is_never_given_again()
    {
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var conversation = new VoiceConversation();
        var tools = new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, conversation);
        servers.AddProject(ServerA, Chat, "testing", root: "Assistant", kind: "chat");
        Assert.StartsWith("testing (", await tools.ProjectStatusAsync("testing", CancellationToken.None));
        Assert.Equal(new ProjectRef(ServerA, Chat), conversation.Current);

        servers.DeleteProject(ServerA, Chat);

        Assert.Empty(projects.Projects);
        Assert.Null(handles.Of(new ProjectRef(ServerA, Chat)));
        Assert.StartsWith("Unknown project 'testing': that project was deleted.", await tools.ProjectStatusAsync("testing", CancellationToken.None));
        // Nor is it the one an unnamed answer goes to
        Assert.StartsWith("No project is being talked about", await tools.AnswerAsync(null, "Fortsæt.", CancellationToken.None));
        Assert.Equal("No projects on any server.", tools.ListProjectsText());
        Assert.Empty(servers.Replies);

        // Another chat named testing: "testing" said from the earlier list must not reach it
        var next = new ProjectRef(ServerA, "Outbound/Assistant/260930-chat-testing-x7k2");
        servers.AddProject(ServerA, next.ProjectId, "testing", root: "Assistant", kind: "chat");
        Assert.Equal("chat", handles.Of(next));
        Assert.StartsWith("Unknown project 'testing': that project was deleted.", await tools.AnswerAsync("testing", "Fortsæt.", CancellationToken.None));
        Assert.Empty(servers.Replies);
    }

    /// <summary>
    /// "chat 2" deleted, then said from an earlier list: it reaches neither "chat" (the words step would find it among
    /// "chat", "2") nor "chat 3" (a Jaro-Winkler neighbour at 0.933), nothing is sent, and no later chat is "chat 2".
    /// </summary>
    [Theory]
    [InlineData("chat 2")]
    [InlineData("Chat 2.")]
    [InlineData("chat to")]
    [InlineData("projekt chat 2")]
    public async Task A_deleted_numbered_handle_said_names_nothing_and_is_never_given_again(string spoken)
    {
        var servers = new FakeServers(ServerA);
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var tools = new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, new VoiceConversation());
        ProjectRef[] chats = [.. new[] { "a1", "b2", "c3" }.Select(suffix => new ProjectRef(ServerA, $"Outbound/Assistant/260930-chat-chat-{suffix}"))];
        foreach (var chat in chats)
            servers.AddProject(ServerA, chat.ProjectId, "chat", root: "Assistant", kind: "chat");
        Assert.Equal(["chat", "chat 2", "chat 3"], chats.Select(handles.Of));

        servers.DeleteProject(ServerA, chats[1].ProjectId);
        Assert.Null(handles.Resolve(spoken));
        Assert.StartsWith($"Unknown project '{spoken}': that project was deleted.", await tools.AnswerAsync(spoken, "Fortsæt.", CancellationToken.None));

        servers.DeleteProject(ServerA, chats[0].ProjectId);
        Assert.Null(handles.Resolve(spoken));
        Assert.StartsWith("Unknown project", await tools.AnswerAsync(spoken, "Fortsæt.", CancellationToken.None));
        Assert.Empty(servers.Replies);

        servers.AddProject(ServerA, "Outbound/Assistant/260930-chat-chat-d4", "chat", root: "Assistant", kind: "chat");
        Assert.Equal("chat 4", handles.Of(new ProjectRef(ServerA, "Outbound/Assistant/260930-chat-chat-d4")));
        Assert.Equal(chats[2], handles.Resolve("chat 3"));
    }

    /// <summary>
    /// A profile renamed pushes its sessions' deletes, then their creates under new IDs: each keeps its handle, so two
    /// of one stem never swap ("chat" and "chat 2" come back in the other order).
    /// </summary>
    [Fact]
    public void A_session_back_under_a_new_ID_keeps_its_handle()
    {
        var servers = new FakeServers(ServerA);
        var handles = new ProjectHandles();
        _ = new ProjectBoard(servers, handles);
        servers.AddProject(ServerA, "Outbound/Assistant/260930-chat-chat-a1", "chat", root: "Assistant", kind: "chat");
        servers.AddProject(ServerA, "Outbound/Assistant/260930-chat-chat-b2", "chat", root: "Assistant", kind: "chat");

        servers.DeleteProject(ServerA, "Outbound/Assistant/260930-chat-chat-a1");
        servers.DeleteProject(ServerA, "Outbound/Assistant/260930-chat-chat-b2");
        servers.AddProject(ServerA, "Private/Assistant/260930-chat-chat-b2", "chat", root: "Assistant", kind: "chat");
        servers.AddProject(ServerA, "Private/Assistant/260930-chat-chat-a1", "chat", root: "Assistant", kind: "chat");

        Assert.Equal("chat", handles.Of(new ProjectRef(ServerA, "Private/Assistant/260930-chat-chat-a1")));
        Assert.Equal("chat 2", handles.Of(new ProjectRef(ServerA, "Private/Assistant/260930-chat-chat-b2")));
    }

    /// <summary>
    /// Handles are the project board's to give: an attention item of a project it has not heard of is not announced,
    /// listed or given a handle, until it has; and a stale list after the delete gives the deleted project none again.
    /// </summary>
    [Fact]
    public async Task Only_the_projects_voice_has_heard_of_get_handles()
    {
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var board = new AttentionBoard(servers, handles, projects);
        var tools = new VoiceTools(servers, board, projects, handles, new VoiceConversation());
        List<string> announced = [];
        board.Attach((_, handle) => { lock (announced) announced.Add(handle); });
        var item = FakeServers.Question(Chat, "testing", "Hvilken profil?");

        servers.PushAttention(ServerA, item);
        Assert.Empty(handles.All);
        Assert.Empty(announced);
        Assert.Equal("Nothing needs the user.", await tools.WhatNeedsMeAsync(CancellationToken.None));

        servers.AddProject(ServerA, Chat, "testing", root: "Assistant", kind: "chat");
        Assert.Equal(["testing"], announced);
        Assert.StartsWith("1 need the user:\n- testing: question",await tools.WhatNeedsMeAsync(CancellationToken.None));

        servers.DeleteProject(ServerA, Chat);
        servers.PushAttention(ServerA, item);   // a stale list, on its own queue, after the delete
        Assert.Empty(handles.All);
        Assert.Null(handles.Of(new ProjectRef(ServerA, Chat)));
        Assert.Equal(["testing"], announced);
    }

    /// <summary>An unknown name gets the projects there are as options, not only those waiting.</summary>
    [Fact]
    public async Task An_unknown_project_is_answered_with_the_projects_there_are()
    {
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var tools = new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, new VoiceConversation());
        servers.AddProject(ServerA, Chat, "testing", root: "Assistant", kind: "chat", profile: "Outbound");

        Assert.Equal("Unknown project 'vonage'. Nothing needs the user now. Projects: testing (testing, Assistant, Outbound, chat): Idle.",
            await tools.ProjectStatusAsync("vonage", CancellationToken.None));
    }
}
