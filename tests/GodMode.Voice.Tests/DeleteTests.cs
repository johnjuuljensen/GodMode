using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Resources;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// Voice deletes sessions (#532): "Slet issue 525 og 526" names the sessions voice calls so, never issues on GitHub. What
/// goes is read back in the code's words, several in one read-back, with what the user should know first (running, an
/// open pull request, only forgotten), and deleted only on the user's yes to it, as on screen: never forced, an adopted
/// one forgotten, its folder kept.
/// </summary>
public sealed class DeleteTests
{
    private const string ServerA = "server-a";
    private const string Id525 = "Godmode/GodMode/261009-issue-525-voice-k7q2";
    private const string Id526 = "Godmode/GodMode/261009-issue-526-voice-m3x9";
    private const string ReadBack = "Skal jeg slette issue 525 og issue 526?";

    private static FakeServers Servers()
    {
        var servers = new FakeServers(ServerA);
        servers.AddProject(ServerA, Id525, "525-voice", root: "GodMode", kind: "issue", profile: "Godmode");
        servers.AddProject(ServerA, Id526, "526-voice", root: "GodMode", kind: "issue", profile: "Godmode");
        return servers;
    }

    private static ScriptedChatClient Delete525And526() => new ScriptedChatClient()
        .CallTool(VoiceTools.DeleteSession, new() { [VoiceTools.ProjectsParameter] = "issue 525; issue 526" });

    private static async Task<OfflineVoice> ReadBackAsync(FakeServers servers, ScriptedChatClient model)
    {
        var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");
        voice.Transcriptions.SayAsRecognized("Slet issue 525 og 526.");
        await voice.Events.SaidAsync(ReadBack);
        return voice;
    }

    [Fact]
    public async Task Two_sessions_are_read_back_together_and_deleted_on_yes()
    {
        var servers = Servers();
        var model = Delete525And526();
        await using var voice = await ReadBackAsync(servers, model);
        Assert.Empty(servers.Deletes);

        voice.Transcriptions.SayAsRecognized("Ja.");
        await voice.Events.SaidAsync("Sletter.");
        await voice.Events.SaidAsync("issue 525 og issue 526 er slettet.");

        Assert.Equal([(new ProjectRef(ServerA, Id525), false), (new ProjectRef(ServerA, Id526), false)], servers.Deletes);
        // The yes is not the model's: it was called for the ask only, and the code read it back
        Assert.Equal(1, model.Calls);
    }

    [Theory]
    [InlineData("Nej.")]
    [InlineData("Vent lidt")]
    [InlineData("Okay.")]
    public async Task Anything_but_a_clear_yes_cancels_and_deletes_nothing(string answer)
    {
        var servers = Servers();
        await using var voice = await ReadBackAsync(servers, Delete525And526());

        voice.Transcriptions.SayAsRecognized(answer);
        await voice.Events.SaidAsync("Annulleret. Intet slettet.");

        Assert.Empty(servers.Deletes);
    }

    [Fact]
    public async Task A_refused_delete_says_which_and_why()
    {
        var servers = Servers();
        servers.DeleteError = "Uncommitted changes.";
        await using var voice = await ReadBackAsync(servers, Delete525And526());

        voice.Transcriptions.SayAsRecognized("Ja.");
        await voice.Events.SaidAsync("issue 525 blev ikke slettet: Uncommitted changes. issue 526 blev ikke slettet: Uncommitted changes.");
    }

    /// <summary>What the user should know before the yes is said first: one runs, one has an open pull request, one is only forgotten.</summary>
    [Fact]
    public async Task The_read_back_says_running_an_open_pull_request_and_forgotten()
    {
        var servers = Servers();
        servers.AddProject(ServerA, "Godmode/GodMode/261009-issue-527-adopt-a1b2", "527-adopt", root: "GodMode", kind: "issue", profile: "Godmode");
        var tools = Tools(servers, out var conversation);
        servers.SetStatus(ServerA, await servers.GetStatusAsync(new ProjectRef(ServerA, Id525), default) with
        {
            PullRequest = new PullRequestStatus("https://example/pr/531", 531, PullRequestState.Open, PullRequestReview.None, DateTime.UtcNow),
        });
        servers.SetStatus(ServerA, await servers.GetStatusAsync(new ProjectRef(ServerA, Id526), default) with { State = ProjectState.Running });
        servers.SetStatus(ServerA, await servers.GetStatusAsync(new ProjectRef(ServerA, "Godmode/GodMode/261009-issue-527-adopt-a1b2"), default) with { Adopted = true });

        var result = await tools.DeleteSessionAsync("525; 526; 527", default);

        Assert.Equal("issue 525 har en åben pull request, nummer 531. issue 526 kører. issue 527 glemmes kun, og dens mappe bliver. " +
            "Skal jeg slette issue 525, issue 526 og issue 527?", conversation.TakeSaid(result));
        Assert.Equal(3, tools.Deletes.Proposed!.Targets.Count);
        Assert.True(tools.Deletes.Proposed.Targets[2].Forget);
    }

    /// <summary>Sessions in different roots are each named with theirs.</summary>
    [Fact]
    public async Task Sessions_of_several_roots_are_each_named_with_theirs()
    {
        var servers = Servers();
        servers.AddProject(ServerA, "Mega/api/261009-issue-12-api-c3d4", "12-api", root: "api", kind: "issue", profile: "Mega");
        var tools = Tools(servers, out var conversation);

        var result = await tools.DeleteSessionAsync("issue 525; issue 12", default);

        // A root with its profile's name is said once (#529)
        Assert.Equal("Skal jeg slette issue 525 i GodMode og issue 12 i api, profil Mega?", conversation.TakeSaid(result));
    }

    [Fact]
    public async Task An_unknown_session_proposes_nothing()
    {
        var tools = Tools(Servers(), out _);

        var result = await tools.DeleteSessionAsync("issue 525; issue 999", default);

        Assert.StartsWith("Nothing will be deleted. Unknown project 'issue 999'.", result);
        Assert.Null(tools.Deletes.Proposed);
    }

    /// <summary>An adopted session is forgotten on the yes, not deleted: its folder stays.</summary>
    [Fact]
    public async Task An_adopted_session_is_forgotten()
    {
        var servers = Servers();
        servers.SetStatus(ServerA, await servers.GetStatusAsync(new ProjectRef(ServerA, Id525), default) with { Adopted = true });
        var model = new ScriptedChatClient().CallTool(VoiceTools.DeleteSession, new() { [VoiceTools.ProjectsParameter] = "issue 525" });
        await using var voice = await OfflineVoice.StartAsync(servers, model);
        await voice.Events.SaidAsync("Klar.");
        voice.Transcriptions.SayAsRecognized("Slet issue 525.");
        await voice.Events.SaidAsync("issue 525 glemmes kun, og dens mappe bliver. Skal jeg slette issue 525, voice?");

        voice.Transcriptions.SayAsRecognized("Ja.");
        await voice.Events.SaidAsync("issue 525 er glemt, mappen er der stadig.");

        Assert.Equal([(new ProjectRef(ServerA, Id525), true)], servers.Deletes);
    }

    private static VoiceTools Tools(FakeServers servers, out VoiceConversation conversation)
    {
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        conversation = new VoiceConversation();
        _ = servers.ConnectAsync(default);
        return new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, conversation,
            phrases: new VoicePhrases(new SessionLanguages("da-DK")));
    }
}
