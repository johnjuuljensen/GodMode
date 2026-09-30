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
        var model = new ScriptedModel()
            .CallTool(VoiceTools.ListProjects).Respond("1 projekt: testing.")
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.ProjectParameter] = "Assistent", [VoiceTools.TextParameter] = "Skift til outbound profil." })
            .Respond("Sendt til testing.");
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");

        servers.AddProject(ServerA, Chat, "testing", root: "Assistant", kind: "chat", profile: "Outbound");

        voice.Transcriptions.Say("Hvilke projekter er i gang?");
        await voice.Events.SaidAsync("1 projekt: testing.");
        Assert.Equal("1 projects:\n- testing (testing, Assistant, Outbound, chat): Idle", Assert.Single(model.ToolResults));

        voice.Transcriptions.Say("Sig til Assistent at den skal skifte til outbound profil");
        await voice.Events.SaidAsync("Sendt til testing.");

        var (project, text) = Assert.Single(servers.Replies);
        Assert.Equal(new ProjectRef(ServerA, Chat), project);
        Assert.Equal("Skift til outbound profil.", text);
    }

    [Fact]
    public async Task A_deleted_session_is_forgotten_and_its_handle_is_free_again()
    {
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var conversation = new VoiceConversation();
        var tools = new VoiceTools(servers, new AttentionBoard(servers, handles), projects, handles, conversation);
        servers.AddProject(ServerA, Chat, "testing", root: "Assistant", kind: "chat");
        Assert.StartsWith("testing (", await tools.ProjectStatusAsync("testing", CancellationToken.None));
        Assert.Equal(new ProjectRef(ServerA, Chat), conversation.Current);

        servers.DeleteProject(ServerA, Chat);

        Assert.Empty(projects.Projects);
        Assert.Null(handles.Of(new ProjectRef(ServerA, Chat)));
        Assert.StartsWith("Unknown project 'testing'.", await tools.ProjectStatusAsync("testing", CancellationToken.None));
        // Nor is it the one an unnamed answer goes to
        Assert.StartsWith("No project is being talked about", await tools.AnswerAsync(null, "Fortsæt.", CancellationToken.None));
        Assert.Equal("No projects on any server.", tools.ListProjectsText());
        Assert.Empty(servers.Replies);

        servers.AddProject(ServerA, "Outbound/Assistant/260930-chat-testing-x7k2", "testing", root: "Assistant", kind: "chat");
        Assert.Equal("testing", handles.Of(new ProjectRef(ServerA, "Outbound/Assistant/260930-chat-testing-x7k2")));
    }

    /// <summary>An unknown name gets the projects there are as options, not only those waiting.</summary>
    [Fact]
    public async Task An_unknown_project_is_answered_with_the_projects_there_are()
    {
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var tools = new VoiceTools(servers, new AttentionBoard(servers, handles), new ProjectBoard(servers, handles), handles, new VoiceConversation());
        servers.AddProject(ServerA, Chat, "testing", root: "Assistant", kind: "chat", profile: "Outbound");

        Assert.Equal("Unknown project 'vonage'. Nothing needs the user now. Projects: testing (testing, Assistant, Outbound, chat): Idle.",
            await tools.ProjectStatusAsync("vonage", CancellationToken.None));
    }
}
