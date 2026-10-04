using System.Collections.Concurrent;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Voice.Tests;

/// <summary>
/// Issue #469: voice leaves a session a live overseer runs (its server-recorded parent, #401) out of what it says unasked:
/// <see cref="VoiceTools.ListProjects"/>, <see cref="VoiceTools.WhatNeedsMe"/>, the announcements and the keyterms, its
/// Question and Error included. The overseer stands for its workers: its own line counts them. Asked for ("overseerens
/// workers", "voice-epics' workers", "alle", a worker by its handle), they are said, a permission request read and never
/// answered. With the overseer gone (deleted, forgotten), its workers are top level again.
/// </summary>
public sealed class OverseerTests
{
    private const string ServerA = "server-a";
    private const string Overseer = "Mega/GodMode/261004-epic-voice-epics-a1b2";
    private const string Asking = "Mega/GodMode/261004-issue-462-queue-c3d4";
    private const string Working = "Mega/GodMode/261004-issue-463-filter-e5f6";
    private const string TopLevel = "Mega/GodMode/261004-issue-500-other-g7h8";

    private sealed record Setup(FakeServers Servers, VoiceTools Tools, VoiceConversation Conversation, ProjectBoard Projects,
        ProjectHandles Handles, ConcurrentQueue<string> Announced);

    /// <summary>The issue's: an overseer with 2 workers, one with a Question; and a top-level session with a Question of its own.</summary>
    private static Setup Fleet(ProjectState overseerState = ProjectState.Idle)
    {
        var servers = new FakeServers(ServerA);
        var handles = new ProjectHandles();
        var projects = new ProjectBoard(servers, handles);
        var board = new AttentionBoard(servers, handles, projects);
        var conversation = new VoiceConversation();
        var tools = new VoiceTools(servers, board, projects, handles, conversation);
        var announced = new ConcurrentQueue<string>();
        board.Attach((item, handle) => announced.Enqueue(handle));

        servers.AddProject(ServerA, Overseer, "voice-epics", root: "GodMode", kind: "epic", profile: "Mega", state: overseerState, outputMinutesAgo: 30);
        servers.AddProject(ServerA, Asking, "462-queue", root: "GodMode", kind: "issue", profile: "Mega", state: ProjectState.WaitingInput, outputMinutesAgo: 5);
        servers.AddProject(ServerA, Working, "463-filter", root: "GodMode", kind: "issue", profile: "Mega", state: ProjectState.Running);
        servers.AddProject(ServerA, TopLevel, "500-other", root: "GodMode", kind: "issue", profile: "Mega", state: ProjectState.WaitingInput, outputMinutesAgo: 10);
        servers.SetRecordedParent(ServerA, Asking, Overseer);
        servers.SetRecordedParent(ServerA, Working, Overseer);
        servers.Set(ServerA,
            FakeServers.Question(Asking, "462-queue", "Skal køen tømmes ved genstart?", minutesAgo: 5) with { Profile = "Mega", Root = "GodMode", RecordedParentId = Overseer },
            FakeServers.Question(TopLevel, "500-other", "Hvilken port?", minutesAgo: 10) with { Profile = "Mega", Root = "GodMode" });
        return new Setup(servers, tools, conversation, projects, handles, announced);
    }

    /// <summary>The issue's test: neither worker is named, and the overseer's line counts the one waiting.</summary>
    [Fact]
    public async Task What_needs_me_names_neither_worker_and_the_overseers_line_counts_them()
    {
        var fleet = Fleet();

        var result = await fleet.Tools.WhatNeedsMeAsync(CancellationToken.None);

        Assert.Equal("2 venter på dig: issue 500 har et spørgsmål. epic voice: 2 workers, 1 venter på den.", fleet.Conversation.TakeSaid(result));
        Assert.Contains("- epic voice: runs 2 workers, 1 of them waiting on it, which it handles", result);
        Assert.DoesNotContain("462", result);
        Assert.DoesNotContain("463", result);
    }

    /// <summary>Only workers wait: the overseer's line alone, and it is what an unnamed reply goes to.</summary>
    [Fact]
    public async Task With_only_workers_waiting_the_overseers_line_is_said_alone()
    {
        var fleet = Fleet();
        fleet.Servers.Set(ServerA,
            FakeServers.Question(Asking, "462-queue", "Skal køen tømmes ved genstart?") with { Profile = "Mega", Root = "GodMode", RecordedParentId = Overseer });

        var result = await fleet.Tools.WhatNeedsMeAsync(CancellationToken.None);

        Assert.Equal("epic voice: 2 workers, 1 venter på den.", fleet.Conversation.TakeSaid(result));
        Assert.Equal(new ProjectRef(ServerA, Overseer), fleet.Conversation.Current);
    }

    /// <summary>The issue's test: "overseerens workers" lists both, with the Question.</summary>
    [Theory]
    [InlineData("voice-epics")]
    [InlineData(VoiceTools.WorkersCurrent)]
    public void The_overseers_workers_are_listed_with_the_question_when_asked(string workers)
    {
        var fleet = Fleet();

        var result = fleet.Tools.ListProjectsText(workers: workers);

        Assert.Equal("2 projekter. Profil Mega, root GodMode: issue 463, issue 462. issue 462 har et spørgsmål.", fleet.Conversation.TakeSaid(result));
        Assert.StartsWith("The workers epic voice runs: ", result);
        Assert.Contains("- issue 462 (462-queue, issue): WaitingInput; needs the user: question: Skal køen tømmes ved genstart?", result);
        Assert.DoesNotContain("issue 500", result);
    }

    /// <summary>What needs me of the overseer's workers names the Question.</summary>
    [Fact]
    public async Task What_needs_me_of_the_overseers_workers_names_the_question()
    {
        var fleet = Fleet();

        var result = await fleet.Tools.WhatNeedsMeAsync(CancellationToken.None, workers: "voice-epics");

        Assert.Equal("issue 462 har et spørgsmål.", fleet.Conversation.TakeSaid(result));
        Assert.Equal(new ProjectRef(ServerA, Asking), fleet.Conversation.Current);
    }

    /// <summary>"Alle" takes in every worker, as every session, in what needs me and in the list.</summary>
    [Fact]
    public async Task All_takes_in_the_workers()
    {
        var fleet = Fleet();

        Assert.Equal("2 venter på dig: issue 462 har et spørgsmål. issue 500 har et spørgsmål.",
            fleet.Conversation.TakeSaid(await fleet.Tools.WhatNeedsMeAsync(CancellationToken.None, workers: VoiceTools.WorkersAll)));
        Assert.Equal("2 venter på dig: issue 462 har et spørgsmål. issue 500 har et spørgsmål.",
            fleet.Conversation.TakeSaid(await fleet.Tools.WhatNeedsMeAsync(CancellationToken.None, since: VoiceTools.SinceAll)));
        Assert.Equal("4 projekter. Profil Mega, root GodMode: issue 463, issue 462, issue 500, epic voice. issue 462 har et spørgsmål.",
            fleet.Conversation.TakeSaid(fleet.Tools.ListProjectsText(workers: VoiceTools.WorkersAll)));
    }

    /// <summary>Unasked, the list leaves the workers out, and the overseer's line counts them.</summary>
    [Fact]
    public void The_list_leaves_the_workers_out_and_the_overseers_line_counts_them()
    {
        var fleet = Fleet();

        var result = fleet.Tools.ListProjectsText();

        Assert.Equal("2 projekter. Profil Mega, root GodMode: epic voice (2 workers, 1 venter på den), issue 500.", fleet.Conversation.TakeSaid(result));
        Assert.Contains("- epic voice (voice-epics, epic): Idle; runs 2 workers, 1 of them waiting on it, which it handles", result);
        Assert.DoesNotContain("issue 46", result);
    }

    /// <summary>The issue's test: the overseer deleted, its workers are listed, and waiting, as usual.</summary>
    [Fact]
    public async Task With_the_overseer_deleted_the_workers_are_top_level_again()
    {
        var fleet = Fleet();
        Assert.Equal(["500"], fleet.Announced);

        fleet.Servers.DeleteProject(ServerA, Overseer);

        Assert.Equal("3 projekter. Profil Mega, root GodMode: issue 463, issue 462, issue 500.", fleet.Conversation.TakeSaid(fleet.Tools.ListProjectsText()));
        Assert.Equal("2 venter på dig: issue 462 har et spørgsmål. issue 500 har et spørgsmål.",
            fleet.Conversation.TakeSaid(await fleet.Tools.WhatNeedsMeAsync(CancellationToken.None)));
        // Its question, held back while the overseer ran it, is announced now
        Assert.Equal(["500", "462"], fleet.Announced);
    }

    /// <summary>A stopped overseer still stands for its workers: it is listed, and they are not.</summary>
    [Fact]
    public async Task A_stopped_overseer_still_stands_for_its_workers()
    {
        var fleet = Fleet(ProjectState.Stopped);

        Assert.Equal("2 venter på dig: issue 500 har et spørgsmål. epic voice: 2 workers, 1 venter på den.",
            fleet.Conversation.TakeSaid(await fleet.Tools.WhatNeedsMeAsync(CancellationToken.None)));
        Assert.Equal(["500"], fleet.Announced);
    }

    /// <summary>A worker's question is not announced; the overseer's own escalation is (#401's escalate).</summary>
    [Fact]
    public async Task The_overseers_own_escalation_is_announced_and_named_and_its_workers_are_not()
    {
        var fleet = Fleet();
        var escalation = new AttentionItem(Overseer, "voice-epics", "Mega", "GodMode", AttentionKind.Escalation, DateTime.UtcNow, "Skal #462 vente på #469?");

        fleet.Servers.Set(ServerA,
            FakeServers.Question(Asking, "462-queue", "Skal køen tømmes ved genstart?") with { Profile = "Mega", Root = "GodMode", RecordedParentId = Overseer },
            escalation);

        Assert.Equal(["500", "voice"], fleet.Announced);
        var result = await fleet.Tools.WhatNeedsMeAsync(CancellationToken.None);
        Assert.Equal("2 venter på dig: epic voice har brug for din beslutning. epic voice: 2 workers, 1 venter på den.", fleet.Conversation.TakeSaid(result));
    }

    /// <summary>
    /// The keyterms are of what voice says: the overseer's handle, not a worker's, nor the root only a worker is in. With
    /// the overseer deleted, they are the worker's again.
    /// </summary>
    [Fact]
    public void The_keyterms_leave_the_workers_out()
    {
        var fleet = Fleet();
        const string Experiment = "Mega/Lab/261004-experiment-vonage-z9y8";
        fleet.Servers.AddProject(ServerA, Experiment, "vonage", root: "Lab", kind: "experiment", profile: "Mega");
        fleet.Servers.SetRecordedParent(ServerA, Experiment, Overseer);
        Assert.Equal("vonage", fleet.Handles.Of(new ProjectRef(ServerA, Experiment)));

        var terms = VoiceSession.Keyterms(fleet.Projects.Shown, fleet.Handles);

        Assert.Contains("voice", terms);
        Assert.DoesNotContain("vonage", terms);
        Assert.DoesNotContain("Lab", terms);

        fleet.Servers.DeleteProject(ServerA, Overseer);
        terms = VoiceSession.Keyterms(fleet.Projects.Shown, fleet.Handles);
        Assert.Contains("vonage", terms);
        Assert.Contains("Lab", terms);
    }

    /// <summary>A worker named by its handle is read as any project, and answered; its permission request is read, never answered.</summary>
    [Fact]
    public async Task A_worker_named_by_its_handle_is_read_and_its_permission_never_answered()
    {
        var fleet = Fleet();

        Assert.Contains("Skal køen tømmes ved genstart?", await fleet.Tools.ProjectStatusAsync("462", CancellationToken.None));

        fleet.Servers.Set(ServerA,
            FakeServers.Permission(Working, "463-filter", "Bash: rm -rf bin") with { Profile = "Mega", Root = "GodMode", RecordedParentId = Overseer });
        fleet.Servers.SetStatus(ServerA, (await fleet.Servers.GetStatusAsync(new ProjectRef(ServerA, Working), CancellationToken.None)) with
        {
            State = ProjectState.WaitingPermission,
            PendingPermission = new PendingPermission("req-1", "Bash", "Bash: rm -rf bin", DateTime.UtcNow),
        });

        Assert.Contains("permission request (Bash: rm -rf bin); answered on screen only", await fleet.Tools.ProjectStatusAsync("463", CancellationToken.None));
        Assert.Contains("answered on screen", await fleet.Tools.AnswerAsync("463", "Ja", CancellationToken.None));
        Assert.Empty(fleet.Servers.Replies);
        Assert.Equal(["500"], fleet.Announced);
    }

    /// <summary>The overseer's own status counts its workers.</summary>
    [Fact]
    public async Task The_overseers_status_counts_its_workers()
    {
        var fleet = Fleet();

        var result = await fleet.Tools.ProjectStatusAsync("voice-epics", CancellationToken.None);

        Assert.Contains($"Runs 2 workers, 1 of them waiting on it, which it handles: {VoiceTools.WhatNeedsMe} or {VoiceTools.ListProjects} with {VoiceTools.WorkersParameter} \"current\" lists them.", result);
    }

    /// <summary>A name that is no overseer lists nothing, and says which overseers there are.</summary>
    [Fact]
    public void Workers_of_a_session_that_runs_none_list_nothing()
    {
        var fleet = Fleet();

        Assert.StartsWith("'500' is no overseer that runs workers now. Nothing was listed. Overseers: epic voice", fleet.Tools.ListProjectsText(workers: "500"));
    }
}
