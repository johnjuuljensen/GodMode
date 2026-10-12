using GodMode.Shared.Models;
using static GodMode.Voice.Tests.FakeServers;

namespace GodMode.Voice.Tests;

/// <summary>
/// <c>read_reply</c> reads what a project said last, from its <c>output.jsonl</c> on the server, whether or not it needs
/// the user (issue #378): in the issue's log (17:11:41) master was Idle, its item seen, and "Læs hele masters svar" got
/// only "Idle." A long reply is read in parts, <c>read_more</c> ("læs videre") giving the next. Reading marks nothing seen.
/// </summary>
public sealed class ReadReplyTests
{
    private const string ServerA = "server-a";
    private const string Master = "p/r/master";
    private static readonly ProjectRef MasterRef = new(ServerA, Master);
    private const string LastAnswer = "Alle tre PR'er er merged, og master bygger grønt.";

    /// <summary>A reply of many sentences, far past one spoken part, but under the cap on what is read.</summary>
    private static readonly string Long = string.Concat(Enumerable.Range(1, 60).Select(i => $"Trin {i}: relayet startede og forbandt uden fejl. ")).Trim();

    private static (FakeServers Servers, VoiceTools Tools) Tools()
    {
        var servers = new FakeServers();
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        return (servers, new VoiceTools(servers, new AttentionBoard(servers, handles, projects), projects, handles, new VoiceConversation()));
    }

    /// <summary>The issue's 17:11:41: master is Idle and needs nothing, and its last reply is read, not "Idle." alone.</summary>
    [Fact]
    public async Task An_idle_project_with_nothing_waiting_gives_its_last_reply()
    {
        var servers = new FakeServers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ReadReply, new() { [VoiceTools.ProjectParameter] = "master" })
            .Respond("Master: alle tre er merged.");
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: _ =>
        {
            servers.AddProject(ServerA, Master, "master");
            servers.SetReplies(ServerA, Master, new AssistantReply("Jeg merger dem nu.", true), new AssistantReply(LastAnswer, true));
            return Task.CompletedTask;
        });
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Læs hele masters svar.");
        await voice.Events.SaidAsync("Master: alle tre er merged.");

        Assert.Equal($"master (master): Idle. Last reply: {LastAnswer}", Assert.Single(model.ToolResults));
        Assert.Equal((MasterRef, 1), Assert.Single(servers.RepliesRead));
    }

    /// <summary>Reading a finished project's reply leaves its item: only the user's own "læst" marks it seen (#379's 17:11:34).</summary>
    [Fact]
    public async Task Reading_a_finished_reply_does_not_mark_it_seen()
    {
        var servers = new FakeServers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ReadReply, new() { [VoiceTools.ProjectParameter] = "master" })
            .Respond("Master: alle tre er merged.");
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: _ =>
        {
            servers.Set(ServerA, Finished(Master, "master", LastAnswer));
            servers.SetReplies(ServerA, Master, new AssistantReply(LastAnswer, true));
            return Task.CompletedTask;
        });
        await voice.Events.SaidAsync("master er færdig.");

        voice.Transcriptions.SayAsRecognized("Er det hele masters svar?");
        await voice.Events.SaidAsync("Master: alle tre er merged.");

        Assert.Contains(LastAnswer, Assert.Single(model.ToolResults));
        Assert.Empty(servers.Seen);
        Assert.Single(await servers.GetAttentionAsync(CancellationToken.None));
    }

    /// <summary>"Læs videre" after a long reply's first part gives the next, from where the first ended, until it is all read.</summary>
    [Fact]
    public async Task A_long_reply_is_read_in_parts_and_read_more_gives_the_next()
    {
        var servers = new FakeServers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ReadReply, new() { [VoiceTools.ProjectParameter] = "master" })
            .Respond("Master, del 1.")
            .CallTool(VoiceTools.ReadMore)
            .Respond("Master, del 2.");
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: _ =>
        {
            servers.AddProject(ServerA, Master, "master");
            servers.SetReplies(ServerA, Master, new AssistantReply(Long, true));
            return Task.CompletedTask;
        });
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Læs masters svar.");
        await voice.Events.SaidAsync("Master, del 1.");
        voice.Transcriptions.SayAsRecognized("Læs videre.");
        await voice.Events.SaidAsync("Master, del 2.");

        var results = model.ToolResults.ToList();
        Assert.Equal(2, results.Count);
        Assert.StartsWith("master (master): Idle. Last reply: Trin 1: ", results[0]);
        Assert.Contains("[Part 1 of ", results[0]);
        Assert.StartsWith("master's reply, part 2 of ", results[1]);
        Assert.DoesNotContain("Trin 1: ", results[1]);
        Assert.Empty(servers.Seen);
    }

    /// <summary>
    /// #411: the project wrote a new reply after the first part of its last was read. "Læs videre" does not read on in
    /// the old one as if it were still the last: the old one is dropped, and the tool says a new one is there.
    /// </summary>
    [Fact]
    public async Task Read_more_after_a_new_reply_does_not_read_on_in_the_old_one()
    {
        var servers = new FakeServers();
        var model = new ScriptedChatClient()
            .CallTool(VoiceTools.ReadReply, new() { [VoiceTools.ProjectParameter] = "master" })
            .Respond("Master, del 1.")
            .CallTool(VoiceTools.ReadMore)
            .Respond("Master har skrevet et nyt svar. Skal jeg læse det?");
        await using var voice = await OfflineVoice.StartAsync(servers, model, connect: _ =>
        {
            servers.AddProject(ServerA, Master, "master");
            servers.SetReplies(ServerA, Master, new AssistantReply(Long, true));
            return Task.CompletedTask;
        });
        await voice.Events.SaidAsync("Klar.");

        voice.Transcriptions.SayAsRecognized("Læs masters svar.");
        await voice.Events.SaidAsync("Master, del 1.");
        servers.SetReplies(ServerA, Master, new AssistantReply(Long, true), new AssistantReply(LastAnswer, true));
        voice.Transcriptions.SayAsRecognized("Læs videre.");
        await voice.Events.SaidAsync("Master har skrevet et nyt svar. Skal jeg læse det?");

        var results = model.ToolResults.ToList();
        Assert.Equal(2, results.Count);
        Assert.StartsWith("master has written a new reply since the one being read", results[1]);
        Assert.DoesNotContain("Trin ", results[1]);
    }

    /// <summary>A reply still being written grows: what was read of it is no longer all it said, and is dropped too.</summary>
    [Fact]
    public async Task Read_more_after_an_unfinished_reply_grew_says_so()
    {
        var (servers, tools) = Tools();
        servers.AddProject(ServerA, Master, "master");
        servers.SetReplies(ServerA, Master, new AssistantReply(Long, false));
        await tools.ReadReplyAsync("master", null, CancellationToken.None);

        servers.SetReplies(ServerA, Master, new AssistantReply(Long + " Trin 61: færdig.", true));

        Assert.StartsWith("master has written a new reply", await tools.ReadMoreAsync(CancellationToken.None));
        Assert.StartsWith("Nothing more to read", await tools.ReadMoreAsync(CancellationToken.None));
    }

    /// <summary>Every part, read in turn, is the whole reply once, each no longer than a spoken part, the last saying it was the end.</summary>
    [Fact]
    public async Task The_parts_together_are_the_whole_reply()
    {
        var (servers, tools) = Tools();
        servers.AddProject(ServerA, Master, "master");
        servers.SetReplies(ServerA, Master, new AssistantReply(Long, true));

        var first = await tools.ReadReplyAsync("master", null, CancellationToken.None);
        var parts = new List<string> { Between(first, "Last reply: ", " [Part 1 of ") };
        var count = int.Parse(Between(first, " [Part 1 of ", ":"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(count, 2, 10);
        for (var k = 2; k <= count; k++)
        {
            var next = await tools.ReadMoreAsync(CancellationToken.None);
            var prefix = $"master's reply, part {k} of {count}: ";
            Assert.StartsWith(prefix, next);
            Assert.EndsWith(k == count ? " [That was the end of it.]" : $" [More follows: {VoiceTools.ReadMore} reads it.]", next);
            parts.Add(next[prefix.Length..next.LastIndexOf(" [", StringComparison.Ordinal)]);
        }

        Assert.Equal(Long, string.Join(" ", parts));
        Assert.All(parts, p => Assert.InRange(p.Length, 1, VoiceTools.ReplyPartLength));
        Assert.StartsWith("Nothing more to read", await tools.ReadMoreAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Several_turns_are_read_oldest_first_and_an_unfinished_one_says_so()
    {
        var (servers, tools) = Tools();
        servers.AddProject(ServerA, Master, "master");
        servers.SetReplies(ServerA, Master, new AssistantReply("Første.", true), new AssistantReply("Det fejlede.", true, IsError: true), new AssistantReply("Jeg arbejder", false));

        var read = await tools.ReadReplyAsync("master", "3", CancellationToken.None);

        Assert.Equal("master (master): Idle. Last 3 replies, oldest first:\nReply 1: Første.\nReply 2 (failed): Det fejlede.\n" +
            "Reply 3 (unfinished: it is still working on it): Jeg arbejder", read);
        Assert.Equal((MasterRef, 3), Assert.Single(servers.RepliesRead));
    }

    [Fact]
    public async Task Turns_are_at_most_the_voices_few()
    {
        var (servers, tools) = Tools();
        servers.AddProject(ServerA, Master, "master");

        await tools.ReadReplyAsync("master", "50", CancellationToken.None);
        await tools.ReadReplyAsync("master", "nul", CancellationToken.None);

        Assert.Equal([(MasterRef, VoiceTools.MaxTurnsRead), (MasterRef, 1)], servers.RepliesRead);
    }

    [Fact]
    public async Task A_project_that_has_said_nothing_says_so()
    {
        var (servers, tools) = Tools();
        servers.AddProject(ServerA, Master, "master");

        Assert.Equal("master (master): Idle. It has said nothing yet.", await tools.ReadReplyAsync("master", null, CancellationToken.None));
    }

    [Fact]
    public async Task An_unknown_project_is_said_unknown_and_nothing_is_read()
    {
        var (servers, tools) = Tools();
        servers.AddProject(ServerA, Master, "master");

        Assert.StartsWith("Unknown project 'nope'.", await tools.ReadReplyAsync("nope", null, CancellationToken.None));
        Assert.Empty(servers.RepliesRead);
    }

    /// <summary>
    /// #523: each part read on names the project as a line said now does (<see cref="ProjectNames.Of"/>), not as the
    /// first part did: right after a part of it, its label alone; after a line about another project, its topic again.
    /// </summary>
    [Fact]
    public async Task Each_part_read_on_is_anchored_as_it_is_said()
    {
        var (servers, tools) = Tools();
        servers.AddProject(ServerA, "p/r/283", "283-voice", kind: "issue");
        servers.AddProject(ServerA, "p/r/101", "101-cleanup", kind: "issue");
        servers.SetReplies(ServerA, "p/r/283", new AssistantReply(Long, true));

        Assert.StartsWith("issue 283, voice (", await tools.ReadReplyAsync("283", null, CancellationToken.None));
        Assert.StartsWith("issue 283's reply, part 2 of ", await tools.ReadMoreAsync(CancellationToken.None));
        // A line about 101 between them, which leaves the reading as it was
        tools.Names.Of(new ProjectRef(ServerA, "p/r/101"));
        Assert.StartsWith("issue 283, voice's reply, part 3 of ", await tools.ReadMoreAsync(CancellationToken.None));
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal) + start.Length;
        return text[from..text.IndexOf(end, from, StringComparison.Ordinal)];
    }
}
