using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Voice.Tests;

/// <summary>
/// Switch, back, peek, stop and resume by voice (#287), as VoiceBot's VoiceControlGraph has them against its fake system:
/// "Skift til 525" makes it the project talked about and shows it in the app, "Tilbage" goes to the one before, "Kig på
/// 526" reads it and leaves the conversation where it was, "Stop 525" and "Genoptag 525" are the screen's Stop and
/// Resume, on a project those buttons would take.
/// </summary>
public sealed class SwitchTests
{
    private const string ServerA = "server-a";
    private const string Id525 = "Godmode/GodMode/261009-issue-525-voice-k7q2";
    private const string Id526 = "Godmode/GodMode/261009-issue-526-voice-m3x9";
    private static readonly ProjectRef P525 = new(ServerA, Id525);
    private static readonly ProjectRef P526 = new(ServerA, Id526);

    private static FakeServers Servers(ProjectState state525 = ProjectState.Idle)
    {
        var servers = new FakeServers(ServerA);
        servers.AddProject(ServerA, Id525, "525-voice", root: "GodMode", kind: "issue", profile: "Godmode", state: state525);
        servers.AddProject(ServerA, Id526, "526-voice", root: "GodMode", kind: "issue", profile: "Godmode");
        return servers;
    }

    private static Dictionary<string, object?> Project(string reference) => new() { [VoiceTools.ProjectParameter] = reference };

    [Fact]
    public async Task Switch_makes_it_the_project_talked_about_and_shows_it_in_the_app()
    {
        var servers = Servers();
        var model = new ScriptedChatClient().CallTool(VoiceTools.SwitchProject, Project("issue 525"));
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Skift til issue 525.");
        await voice.Events.SaidAsync("Skiftet til issue 525, voice. Den er idle.");

        Assert.Equal([(P525, "Godmode")], voice.Events.Shown);
        Assert.Equal(P525, voice.Session.Conversation.Current);
        // The code said it: the model was called for the switch alone
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task Back_goes_to_the_project_before_the_last_switch_and_shows_it()
    {
        var servers = Servers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.SwitchProject, Project("issue 525"))
            .CallTool(VoiceTools.SwitchProject, Project("issue 526"))
            .CallTool(VoiceTools.GoBack);
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");
        voice.Transcriptions.SayAsRecognized("Skift til issue 525.");
        await voice.Events.SaidAsync("Skiftet til issue 525, voice. Den er idle.");
        voice.Transcriptions.SayAsRecognized("Skift til issue 526.");
        await voice.Events.SaidAsync("Skiftet til issue 526, voice. Den er idle.");

        voice.Transcriptions.SayAsRecognized("Tilbage.");
        await voice.Events.SaidAsync("Tilbage til issue 525, voice. Den er idle.");

        Assert.Equal([(P525, "Godmode"), (P526, "Godmode"), (P525, "Godmode")], voice.Events.Shown);
        Assert.Equal(P525, voice.Session.Conversation.Current);
    }

    [Fact]
    public async Task Back_with_nowhere_to_go_says_so_and_shows_nothing()
    {
        var model = new ScriptedChatClient().CallTool(VoiceTools.GoBack);
        await using var voice = await OfflineVoice.StartAsync(Servers(), model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Tilbage.");
        await voice.Events.SaidAsync("Intet at gå tilbage til.");

        Assert.Empty(voice.Events.Shown);
    }

    /// <summary>A peek reads the project, and an answer after it that names none goes where the conversation was.</summary>
    [Fact]
    public async Task Peek_reads_a_project_and_leaves_the_conversation_where_it_was()
    {
        var servers = Servers();
        servers.SetStatus(ServerA, await servers.GetStatusAsync(P526, default) with { Recap = "Venter på review." });
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.SwitchProject, Project("issue 525"))
            .CallTool(VoiceTools.PeekProject, Project("issue 526"))
            .CallTool(VoiceTools.Answer, new() { [VoiceTools.TextParameter] = "Kør videre." });
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");
        voice.Transcriptions.SayAsRecognized("Skift til issue 525.");
        await voice.Events.SaidAsync("Skiftet til issue 525, voice. Den er idle.");

        voice.Transcriptions.SayAsRecognized("Kig på issue 526.");
        await voice.Events.SaidAsync("issue 526, voice: Venter på review.");
        Assert.Equal(P525, voice.Session.Conversation.Current);

        voice.Transcriptions.SayAsRecognized("Svar at den skal køre videre.");
        await voice.Events.SaidAsync("Sendt til issue 525, voice.");

        Assert.Equal([(P525, "Kør videre.")], servers.Replies);
        // A peek moves nothing on screen
        Assert.Equal([(P525, "Godmode")], voice.Events.Shown);
    }

    private const string StopReadBack = "Skal jeg stoppe issue 525, voice?";

    /// <summary>As the app's Stop asks first: the code reads back what stops, and only the yes stops it.</summary>
    [Fact]
    public async Task Stop_is_read_back_and_stops_a_running_project_on_yes()
    {
        var servers = Servers(state525: ProjectState.Running);
        var model = new ScriptedChatClient().CallTool(VoiceTools.StopProject, Project("issue 525"));
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");
        voice.Transcriptions.SayAsRecognized("Stop issue 525.");
        await voice.Events.SaidAsync(StopReadBack);
        Assert.Empty(servers.Stops);

        voice.Transcriptions.SayAsRecognized("Ja.");
        await voice.Events.SaidAsync("Stopper.");
        await voice.Events.SaidAsync("issue 525 er stoppet. Sig genoptag, så fortsætter den.");

        Assert.Equal([P525], servers.Stops);
        // The yes is not the model's: it was called for the stop only, and the code read it back
        Assert.Equal(1, model.Calls);
    }

    /// <summary>
    /// A stop the user does not confirm ("stop den" said to cut the bot off, then anything but yes) stops nothing: a no
    /// cancels it, other words cancel it and go on to the chat.
    /// </summary>
    [Theory]
    [InlineData("Nej.", "Annulleret. Intet stoppet.")]
    [InlineData("Læs videre.", "Læser videre.")]
    public async Task A_stop_not_confirmed_stops_nothing(string answer, string said)
    {
        var servers = Servers(state525: ProjectState.Running);
        var model = new ScriptedChatClient().CallTool(VoiceTools.StopProject, Project("issue 525")).Respond("Læser videre.");
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");
        voice.Transcriptions.SayAsRecognized("Stop den.");
        await voice.Events.SaidAsync(StopReadBack);

        voice.Transcriptions.SayAsRecognized(answer);
        await voice.Events.SaidAsync(said);

        Assert.Empty(servers.Stops);
    }

    /// <summary>As the screen's Stop: a project that does not run has nothing to stop, and is left as it is.</summary>
    [Fact]
    public async Task Stop_of_a_project_that_does_not_run_stops_nothing()
    {
        var servers = Servers();
        var model = new ScriptedChatClient().CallTool(VoiceTools.StopProject, Project("issue 525"));
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Stop issue 525.");
        await voice.Events.SaidAsync("issue 525, voice, kører ikke. Den er idle.");

        Assert.Empty(servers.Stops);
    }

    [Fact]
    public async Task Resume_resumes_a_stopped_project()
    {
        var servers = Servers(state525: ProjectState.Stopped);
        var model = new ScriptedChatClient().CallTool(VoiceTools.ResumeProject, Project("issue 525"));
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Genoptag issue 525.");
        await voice.Events.SaidAsync("issue 525, voice, er genoptaget.");

        Assert.Equal([P525], servers.Resumes);
    }

    /// <summary>A resume of one that is not stopped would send it "Continue", a turn the user never wrote: it is left as it is.</summary>
    [Fact]
    public async Task Resume_of_a_project_that_is_not_stopped_resumes_nothing()
    {
        var servers = Servers();
        var model = new ScriptedChatClient().CallTool(VoiceTools.ResumeProject, Project("issue 525"));
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Genoptag issue 525.");
        await voice.Events.SaidAsync("issue 525, voice, er ikke stoppet. Den er idle.");

        Assert.Empty(servers.Resumes);
    }

    /// <summary>A stop that names no project, right after an announcement changed the project talked about, asks which (#461).</summary>
    [Fact]
    public async Task An_unnamed_stop_just_after_an_announcement_switched_project_asks_which()
    {
        var servers = Servers(state525: ProjectState.Running);
        var (tools, conversation) = Tools(servers);
        conversation.Current = P526;
        conversation.Announced(P525);

        var result = await tools.StopProjectAsync(null, default);

        Assert.Empty(servers.Stops);
        Assert.StartsWith("Nothing was stopped", result);
    }

    private static (VoiceTools, VoiceConversation) Tools(FakeServers servers)
    {
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var conversation = new VoiceConversation();
        _ = servers.ConnectAsync(default);
        return (new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, conversation,
            phrases: new VoicePhrases(new VoiceBot.Core.Resources.SessionLanguages("da-DK"))), conversation);
    }
}
