using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Voice.Tests;

/// <summary>
/// Issue #513: when the user asks about a project (<c>project_status</c>) that keeps no recap, voice asks it for one,
/// says it has, and reads the recap the next time it is asked about. Never a project that has one, runs, or waits on the
/// user; the server sends <c>/recap</c> once, whoever asks.
/// </summary>
public sealed class RecapAskTests
{
    private const string ServerA = "server-a";
    private const string Result = "Færdig med migrationen.";

    private static ProjectStatus Status(ProjectState state, string? recap = null, string? question = null) =>
        new("p/r/283", "283-voice", state, DateTime.UtcNow, DateTime.UtcNow, question, new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0),
            null, null, 0, RootName: "root", ProfileName: "Default", LastResult: Result, LastResultAt: DateTime.UtcNow,
            Recap: recap, RecapAt: recap is null ? null : DateTime.UtcNow);

    private static (FakeServers Servers, VoiceTools Tools, VoiceConversation Conversation) Tools(ProjectStatus status, RecapAsk answer = RecapAsk.Sent)
    {
        var servers = new FakeServers { RecapAnswer = answer };
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var conversation = new VoiceConversation();
        var tools = new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, conversation);
        servers.SetStatus(ServerA, status);
        return (servers, tools, conversation);
    }

    [Fact]
    public async Task An_idle_project_with_no_recap_is_asked_for_one_and_voice_says_it_has_asked()
    {
        var (servers, tools, conversation) = Tools(Status(ProjectState.Idle));

        var result = await tools.ProjectStatusAsync("283", CancellationToken.None);

        Assert.Equal(new ProjectRef(ServerA, "p/r/283"), Assert.Single(servers.RecapAsks));
        Assert.EndsWith("you have asked it for one, which is read the next time the user asks about it.", result);
        Assert.Equal($"Sidste resultat fra issue 283: {Result} Jeg har bedt den om et resumé, som jeg læser næste gang du spørger.",
            conversation.TakeSaid(result));
    }

    [Fact]
    public async Task Asked_before_voice_says_the_recap_has_not_come()
    {
        var (_, tools, conversation) = Tools(Status(ProjectState.Idle), RecapAsk.Asked);

        var result = await tools.ProjectStatusAsync("283", CancellationToken.None);

        Assert.EndsWith("It has been asked for a recap of where it stands, which has not come yet.", result);
        Assert.EndsWith("Den er bedt om et resumé, som ikke er kommet endnu.", conversation.TakeSaid(result));
    }

    [Fact]
    public async Task The_recap_once_it_has_come_is_read_and_nothing_is_asked()
    {
        var (servers, tools, conversation) = Tools(Status(ProjectState.Idle, recap: "Pull request 456 er åben."));

        var result = await tools.ProjectStatusAsync("283", CancellationToken.None);

        Assert.Empty(servers.RecapAsks);
        Assert.Equal("issue 283: Pull request 456 er åben.", conversation.TakeSaid(result));
    }

    [Theory]
    [InlineData(ProjectState.Running)]
    [InlineData(ProjectState.WaitingInput)]
    [InlineData(ProjectState.WaitingPermission)]
    [InlineData(ProjectState.Stopped)]
    [InlineData(ProjectState.Error)]
    public async Task A_project_that_is_not_idle_is_not_asked(ProjectState state)
    {
        var (servers, tools, _) = Tools(Status(state));

        var result = await tools.ProjectStatusAsync("283", CancellationToken.None);

        Assert.Empty(servers.RecapAsks);
        Assert.DoesNotContain("recap", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_idle_project_that_asks_the_user_is_not_asked()
    {
        var (servers, tools, _) = Tools(Status(ProjectState.Idle, question: "Skal jeg merge?"));

        await tools.ProjectStatusAsync("283", CancellationToken.None);

        Assert.Empty(servers.RecapAsks);
    }

    [Fact]
    public async Task A_server_that_sends_nothing_leaves_the_status_as_it_was()
    {
        var (servers, tools, conversation) = Tools(Status(ProjectState.Idle), RecapAsk.Busy);

        var result = await tools.ProjectStatusAsync("283", CancellationToken.None);

        Assert.Single(servers.RecapAsks);
        Assert.DoesNotContain("recap", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"Sidste resultat fra issue 283: {Result}", conversation.TakeSaid(result));
    }
}
