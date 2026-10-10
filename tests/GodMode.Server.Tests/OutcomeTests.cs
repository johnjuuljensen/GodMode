using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Shared;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.Server.Tests;

/// <summary>
/// A turn's outcome (issue #467): the session says with its <c>speak</c> call how its turn ends, which the status keeps
/// (<see cref="ProjectStatus.Outcome"/>) and attention follows. Done raises Finished, needs-you and blocked a Question,
/// continuing nothing; with none, Finished as before. A merged pull request is done whatever the session said.
/// End to end against the real server with FakeClaude, and the rules on statuses as status.json has them.
/// </summary>
public class OutcomeTests
{
    private const string Spoken = "Pull requesten er åbnet, og testene kører.";

    /// <summary>A session whose first turn speaks with <paramref name="outcome"/> and ends with <paramref name="reply"/>.</summary>
    private static async Task<(FleetRun Run, string Id, ProjectStatus Ended)> EndATurnAsync(string? outcome, string reply = "Pull requesten er åbnet.")
    {
        var script = new FakeScript().EmitInit()
            .AwaitStdin().EmitUser("Start", echo: true).Speak(Spoken, outcome: outcome).EmitAssistant(reply).EmitResult(reply)
            .AwaitStdin();
        return await (await FleetRun.StartAsync(script)).SetUpAsync(async run =>
        {
            var id = await run.CreateOverHubAsync("worker");
            var ended = await run.Client.WaitForAsync(id, s => s is { LastResult: not null, State: not ProjectState.Running }, run.Server);
            return (run, id, ended);
        });
    }

    private static async Task<AttentionItem?> ItemAsync(FleetRun run, string id) =>
        (await run.Client.Hub.InvokeAsync<AttentionItem[]>(nameof(IProjectHub.GetAttention))).SingleOrDefault(i => i.ProjectId == id);

    private static async Task<ProjectSummary> SummaryAsync(FleetRun run, string id) =>
        Assert.Single(await run.Client.Hub.InvokeAsync<ProjectSummary[]>(nameof(IProjectHub.ListProjects)), p => p.Id == id);

    [Fact]
    public async Task AContinuingTurn_IsIdle_AndRaisesNothing()
    {
        var (run, id, ended) = await EndATurnAsync("continuing");
        await using var _ = run;

        Assert.Equal((ProjectState.Idle, TurnOutcome.Continuing), (ended.State, ended.Outcome));
        Assert.Null(await ItemAsync(run, id));
        Assert.Equal(TurnOutcome.Continuing, (await SummaryAsync(run, id)).Outcome);
    }

    [Fact]
    public async Task ADoneTurn_RaisesFinished_SaidAsDone()
    {
        var (run, id, ended) = await EndATurnAsync("done");
        await using var _ = run;

        Assert.Equal((ProjectState.Idle, TurnOutcome.Done), (ended.State, ended.Outcome));
        var item = await ItemAsync(run, id);
        Assert.Equal((AttentionKind.Finished, TurnOutcome.Done, Spoken), (item?.Kind, item?.Outcome, item?.Spoken));
        Assert.Equal(TurnOutcome.Done, (await SummaryAsync(run, id)).Outcome);
    }

    /// <summary>A turn that gave no outcome raises Finished as every turn did before outcomes, with none: idle, not done.</summary>
    [Fact]
    public async Task ATurnWithNoOutcome_RaisesFinished_WithNone()
    {
        var (run, id, ended) = await EndATurnAsync(null);
        await using var _ = run;

        Assert.Equal(ProjectState.Idle, ended.State);
        Assert.Null(ended.Outcome);
        var item = await ItemAsync(run, id);
        Assert.Equal(AttentionKind.Finished, item?.Kind);
        Assert.Null(item?.Outcome);
    }

    /// <summary>A turn that needs the user, or is blocked, waits on the user as a question does, with no '?' in it.</summary>
    [Theory]
    [InlineData("needs-you", TurnOutcome.NeedsYou)]
    [InlineData("blocked", TurnOutcome.Blocked)]
    public async Task ATurnThatNeedsTheUser_OrIsBlocked_IsAQuestion(string outcome, TurnOutcome said)
    {
        const string reply = "Jeg mangler adgang til repoet.";
        var (run, id, ended) = await EndATurnAsync(outcome, reply);
        await using var _ = run;

        Assert.Equal((ProjectState.WaitingInput, said, reply), (ended.State, ended.Outcome, ended.CurrentQuestion));
        var item = await ItemAsync(run, id);
        Assert.Equal((AttentionKind.Question, said, reply), (item?.Kind, item?.Outcome, item?.Text));
    }

    /// <summary>An outcome that is not one of the four refuses the call, with why, and the turn keeps none of it.</summary>
    [Fact]
    public async Task AnUnknownOutcome_RefusesTheCall()
    {
        var script = new FakeScript().EmitInit()
            .AwaitStdin().EmitUser("Start", echo: true).Speak(Spoken, outcome: "finished", refused: true)
            .EmitAssistant("Færdig.").EmitResult("done").AwaitStdin();
        await using var run = await FleetRun.StartAsync(script);
        var id = await run.CreateOverHubAsync("worker");

        var ended = await run.Client.WaitForAsync(id, s => s.State == ProjectState.Idle, run.Server);
        Assert.Null(ended.Outcome);
        Assert.Null(ended.SpokenSummary);
        var call = Assert.Single((await run.WaitForLaunchAsync(id, l => l.Calls.Count > 0)).Calls);
        Assert.Contains("The outcome 'finished' is not one GodMode knows", call);
    }

    /// <summary>The next turn's start clears the last one's outcome, as it clears its spoken reply.</summary>
    [Fact]
    public async Task TheNextTurn_ClearsTheOutcome()
    {
        var go = Path.Combine(Path.GetTempPath(), $"outcome-go-{Guid.NewGuid():N}");
        var script = new FakeScript().EmitInit()
            .AwaitStdin().EmitUser("Start", echo: true).Speak(Spoken, outcome: "done").EmitAssistant("Færdig.").EmitResult("done")
            .AwaitStdin().EmitUser("Mere", echo: true).AwaitFile(go).EmitAssistant("Ok.").EmitResult("more")
            .AwaitStdin();
        await using var run = await FleetRun.StartAsync(script);
        var id = await run.CreateOverHubAsync("worker");
        await run.Client.WaitForAsync(id, s => s.Outcome == TurnOutcome.Done, run.Server);

        await run.Client.Hub.InvokeAsync(nameof(IProjectHub.ReplyAndResume), id, "Mere");
        await run.Client.WaitForAsync(id, s => s is { State: ProjectState.Running, Outcome: null }, run.Server);
        File.WriteAllText(go, "");
        var ended = await run.Client.WaitForAsync(id, s => s is { State: ProjectState.Idle, LastResult: "more" }, run.Server);
        Assert.Null(ended.Outcome);
        File.Delete(go);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(" ", null, null)]
    [InlineData("done", TurnOutcome.Done, null)]
    [InlineData("needs-you", TurnOutcome.NeedsYou, null)]
    [InlineData("Continuing", TurnOutcome.Continuing, null)]
    [InlineData(" blocked ", TurnOutcome.Blocked, null)]
    [InlineData("finished", null, "The outcome 'finished' is not one GodMode knows: give done, needs-you, continuing, blocked.")]
    public void CheckOutcome_TakesTheFour_NoneForBlank_OrSaysWhyNot(string? given, TurnOutcome? outcome, string? refused) =>
        Assert.Equal((outcome, refused), SpeakTool.CheckOutcome(given));

    private static readonly DateTime Ended = new(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);

    private static ProjectStatus IdleAfter(TurnOutcome? outcome, bool quiet = false) =>
        new("p/r/1", "worker", ProjectState.Idle, Ended.AddDays(-1), Ended, null, new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0,
            LastResult: "Waiting on CI.", LastResultAt: Ended, Outcome: outcome, QuietResult: quiet);

    private static PullRequestStatus PullRequest(PullRequestState state, DateTime changedAt) =>
        new("https://example.test/pr/7", 7, state, PullRequestReview.Approved, changedAt);

    /// <summary>A merged pull request is done, whatever the session said: a continuing turn's included, since it merged.</summary>
    [Fact]
    public void AMergedPullRequest_IsDone_WhateverTheTurnSaid()
    {
        var merged = IdleAfter(TurnOutcome.Continuing, quiet: true) with { PullRequest = PullRequest(PullRequestState.Merged, Ended.AddMinutes(5)) };

        var item = Attention.Of(merged);
        Assert.Equal((AttentionKind.Finished, TurnOutcome.Done, Ended.AddMinutes(5)), (item?.Kind, item?.Outcome, item?.Since));
        Assert.Equal("Pull request #7 is merged.", item?.Text);
        Assert.Equal(TurnOutcome.Done, merged.EffectiveOutcome);
        // Seen since it merged, it needs the user no more
        Assert.Null(Attention.Of(merged with { SeenAt = Ended.AddMinutes(6) }));
        // Open, the turn's continuing raises nothing
        Assert.Null(Attention.Of(merged with { PullRequest = PullRequest(PullRequestState.Open, Ended.AddMinutes(5)) }));
        // A turn with no outcome is done once it merged too
        Assert.Equal(TurnOutcome.Done, Attention.Of(IdleAfter(null) with { PullRequest = merged.PullRequest })?.Outcome);
    }

    /// <summary>
    /// The merge is news once: continuing and quiet turns that end after it raise no new item (#401), and while it is
    /// unseen they leave its time as it was.
    /// </summary>
    [Theory]
    [InlineData(TurnOutcome.Continuing)]
    [InlineData(null)]
    public void AfterAMerge_AContinuingOrQuietTurn_RaisesNoNewItem(TurnOutcome? outcome)
    {
        var mergedAt = Ended.AddMinutes(5);
        var later = IdleAfter(outcome, quiet: true) with
        {
            LastResultAt = Ended.AddHours(1), PullRequest = PullRequest(PullRequestState.Merged, mergedAt),
        };

        // Seen since it merged: the later turn raises nothing
        Assert.Null(Attention.Of(later with { SeenAt = mergedAt.AddMinutes(1) }));
        // Unseen: still the merge's one item, at its own time
        var item = Attention.Of(later);
        Assert.Equal((AttentionKind.Finished, TurnOutcome.Done, mergedAt), (item?.Kind, item?.Outcome, item?.Since));
    }

    /// <summary>A done turn whose pull request merged keeps its own result and spoken reply.</summary>
    [Fact]
    public void ADoneTurn_WithItsPullRequestMerged_KeepsItsOwnResult()
    {
        var done = IdleAfter(TurnOutcome.Done) with { SpokenSummary = Spoken, PullRequest = PullRequest(PullRequestState.Merged, Ended.AddMinutes(-5)) };

        var item = Attention.Of(done);
        Assert.Equal((AttentionKind.Finished, TurnOutcome.Done, "Waiting on CI.", Spoken, Ended), (item?.Kind, item?.Outcome, item?.Text, item?.Spoken, item?.Since));
    }

    /// <summary>status.json keeps the outcome by its name on the wire, and reads it back.</summary>
    [Fact]
    public void TheOutcome_IsKeptInStatusJson_ByItsName()
    {
        var json = JsonSerializer.Serialize(IdleAfter(TurnOutcome.NeedsYou), JsonDefaults.Options);

        Assert.Contains("\"needs-you\"", json);
        Assert.Equal(TurnOutcome.NeedsYou, JsonSerializer.Deserialize<ProjectStatus>(json, JsonDefaults.Options)!.Outcome);
    }
}
