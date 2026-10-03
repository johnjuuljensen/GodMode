using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Pipeline;
using VoiceBot.Core.Resources;

namespace GodMode.Voice.Tests;

/// <summary>
/// Voice and the tiers (issue #438): an item for the inbox alone is listed when asked and never announced, an important
/// project's announcement is said before the others, and "marker X som vigtig" sets the project's tier.
/// </summary>
public class ImportanceTests
{
    private const string ServerA = "server-a";
    private static readonly SessionLanguages Danish = new("da-DK");

    private static (FakeServers Servers, AttentionBoard Board, VoiceTools Tools, VoiceConversation Conversation) Voice()
    {
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var board = new AttentionBoard(servers, handles, projects);
        var conversation = new VoiceConversation();
        return (servers, board, new VoiceTools(servers, board, projects, handles, conversation), conversation);
    }

    [Fact]
    public async Task An_item_for_the_inbox_alone_is_listed_but_never_announced()
    {
        var (servers, board, tools, _) = Voice();
        List<string> announced = [];
        board.Attach((item, _) => { lock (announced) announced.Add(item.Item.ProjectId); });
        servers.AddProject(ServerA, "quiet", "quiet one", root: "GodMode", kind: "chat");
        servers.AddProject(ServerA, "loud", "loud one", root: "GodMode", kind: "chat");

        servers.PushAttention(ServerA,
            FakeServers.Finished("quiet", "quiet one", "Done.") with { Importance = Importance.Quiet, Alert = AttentionAlert.Inbox },
            FakeServers.Finished("loud", "loud one", "Done."));

        Assert.Equal(["loud"], announced);
        Assert.StartsWith("2 need the user:", await tools.WhatNeedsMeAsync(CancellationToken.None));
    }

    [Fact]
    public void An_important_projects_announcement_is_said_first()
    {
        var (servers, board, _, conversation) = Voice();
        servers.AddProject(ServerA, "first", "first one", root: "GodMode", kind: "chat");
        servers.AddProject(ServerA, "vital", "vital one", root: "GodMode", kind: "chat");
        servers.PushAttention(ServerA,
            FakeServers.Finished("first", "first one", "Done.", minutesAgo: 10),
            FakeServers.Finished("vital", "vital one", "Done.") with { Importance = Importance.Important, Alert = AttentionAlert.Interrupt });
        var formatter = new GodModeAnnouncementFormatter(new VoicePhrases(Danish), conversation, board);

        var said = formatter.Format([
            new Announcement("first er færdig", new ProjectRef(ServerA, "first").Key),
            new Announcement("vital er færdig", new ProjectRef(ServerA, "vital").Key),
        ], Danish);

        Assert.True(said.IndexOf("vital", StringComparison.Ordinal) < said.IndexOf("first", StringComparison.Ordinal), said);
    }

    [Theory]
    [InlineData("vigtig", Importance.Important)]
    [InlineData("important", Importance.Important)]
    [InlineData("Normal", Importance.Normal)]
    [InlineData("stille", Importance.Quiet)]
    public async Task Marking_a_project_sets_its_tier(string said, Importance tier)
    {
        var (servers, _, tools, conversation) = Voice();
        servers.AddProject(ServerA, "p1", "backup job", root: "GodMode", kind: "chat");

        var reply = await tools.SetImportanceAsync("backup job", said, CancellationToken.None);

        Assert.Equal((new ProjectRef(ServerA, "p1"), tier), Assert.Single(servers.Importances));
        Assert.Contains(tier.ToString().ToLowerInvariant(), reply);
        Assert.Equal(new ProjectRef(ServerA, "p1"), conversation.Current);
    }

    [Fact]
    public async Task A_word_that_is_no_tier_changes_nothing()
    {
        var (servers, _, tools, _) = Voice();
        servers.AddProject(ServerA, "p1", "backup job", root: "GodMode", kind: "chat");

        var reply = await tools.SetImportanceAsync("backup job", "loud", CancellationToken.None);

        Assert.Empty(servers.Importances);
        Assert.Contains("Nothing was changed", reply);
    }
}
