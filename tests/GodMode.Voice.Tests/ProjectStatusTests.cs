using System.Globalization;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// <c>project_status</c> reads a project's question or result in full (issue #377), not the attention item's text, which
/// the server cuts at 500 characters for lists and notifications. As in the issue's log (17:06:51): a reply of about
/// 2,000 characters that ends on its question.
/// </summary>
public sealed class ProjectStatusTests
{
    private const string ServerA = "server-a";
    private const string Id = "p/r/283";
    private const string Ending = "Skal jeg merge PR'en nu, eller vil du se relay-loggen først?";
    private static readonly string Long =
        string.Concat(Enumerable.Range(1, 40).Select(i => $"Trin {i}: relayet startede og forbandt uden fejl. ")) + Ending;

    /// <summary>The text as the server's attention item has it: cut at a word to about 500 characters.</summary>
    private static string Cut(string text) => text[..text.LastIndexOf(' ', 499)] + "…";

    private static ProjectStatus Status(ProjectState state, string? question = null, string? result = null) =>
        new(Id, "283-voice", state, DateTime.UtcNow, DateTime.UtcNow, question, new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0),
            null, null, 0, RootName: "root", ProfileName: "Default", LastResult: result,
            LastResultAt: result is null ? null : DateTime.UtcNow);

    [Fact]
    public async Task A_long_question_is_read_in_full_through_project_status()
    {
        Assert.True(Long.Length > 2000);
        var servers = new FakeServers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ProjectStatus, new() { [VoiceTools.ProjectParameter] = "283" })
            .Respond("issue 283 spørger om den skal merge.");
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: _ =>
        {
            servers.Set(ServerA, Question(Id, "283-voice", Cut(Long)));
            servers.SetStatus(ServerA, Status(ProjectState.WaitingInput, question: Long));
            return Task.CompletedTask;
        });
        await voice.Events.SaidAsync("issue 283 har et spørgsmål.");

        voice.Transcriptions.SayAsRecognized("Hvad spørger 283 om?");
        await voice.Events.SaidAsync("issue 283 spørger om den skal merge.");

        var result = Assert.Single(model.ToolResults);
        Assert.Contains(Long, result);
        Assert.EndsWith(Ending, result);
        Assert.StartsWith("issue 283 (", result);
    }

    [Fact]
    public async Task A_long_result_is_read_in_full()
    {
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var tools = new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, new VoiceConversation());
        servers.Set(ServerA, Finished(Id, "283-voice", Cut(Long)));
        servers.SetStatus(ServerA, Status(ProjectState.Idle, result: Long));

        var result = await tools.ProjectStatusAsync("283", CancellationToken.None);

        Assert.Equal($"issue 283 (283-voice): Idle. Needs the user: finished: {Long}", result);
    }

    /// <summary>A result far past any reply read whole keeps its start and its end, where the question is, and says it was cut.</summary>
    [Fact]
    public async Task A_result_over_the_limit_keeps_its_start_and_end_and_says_it_was_cut()
    {
        var huge = "Start på svaret. " + new string('x', 3 * VoiceTools.MaxStatusTextLength) + " " + Ending;
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var tools = new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, new VoiceConversation());
        servers.Set(ServerA, Finished(Id, "283-voice", Cut(huge)));
        servers.SetStatus(ServerA, Status(ProjectState.Idle, result: huge));

        var result = await tools.ProjectStatusAsync("283", CancellationToken.None);

        Assert.StartsWith("issue 283 (283-voice): Idle. Needs the user: finished: Start på svaret. ", result);
        Assert.EndsWith(Ending, result);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"[... {huge.Length - VoiceTools.MaxStatusTextLength} characters cut here;"), result);
        Assert.InRange(result.Length, VoiceTools.MaxStatusTextLength, VoiceTools.MaxStatusTextLength + 200);
    }

    /// <summary>A project in error says its error once, in full: not the item's cut text and the error again.</summary>
    [Fact]
    public async Task An_error_is_said_once_in_full()
    {
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var tools = new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, new VoiceConversation());
        servers.Set(ServerA, new AttentionItem(Id, "283-voice", "Default", "root", AttentionKind.Error, DateTime.UtcNow, Cut(Long)));
        servers.SetStatus(ServerA, Status(ProjectState.Error) with { LastError = Long });

        Assert.Equal($"issue 283 (283-voice): Error. Needs the user: failed: {Long}",
            await tools.ProjectStatusAsync("283", CancellationToken.None));
    }
}
