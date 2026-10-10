using GodMode.Shared.Models;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// Issue #289: a permission prompt that arrives between voice reading a session's status and its answer reaching the
/// server is not denied with the spoken words. The server refuses the reply, and voice says the session waits on a
/// permission request, answered on screen, as it says when it finds one before it answers.
/// </summary>
public sealed class PermissionRaceTests
{
    private const string Server = "server-a";
    private const string Id = "Default/root/283-voice";

    [Fact]
    public async Task A_permission_that_arrives_mid_answer_is_left_for_the_screen_and_said()
    {
        var servers = new FakeServers(Server);
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var tools = new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, new VoiceConversation());
        servers.Set(Server, Question(Id, "283-voice", "Hvilken branch?"));
        servers.PermissionBeforeReply = new PendingPermission("req-2", "Bash", "Bash: git push --force", DateTime.UtcNow);

        var result = await tools.AnswerAsync("283", "master", CancellationToken.None);

        Assert.Contains("waiting on a permission request (Bash: git push --force), which is answered on screen", result);
        Assert.Contains("Nothing was sent", result);
        Assert.Empty(servers.Replies);
    }
}
