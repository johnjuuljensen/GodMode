using System.Collections.Concurrent;
using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Server.Tests.Lifecycle;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using ModelContextProtocol.Client;
using static GodMode.Server.Tests.FleetRun;

namespace GodMode.Server.Tests;

/// <summary>
/// An overseer deletes a worker it is done with (issue #428), against the real server with FakeClaude: the fleet's
/// <c>delete_session</c> runs the user's delete, never forced, on the caller's own descendants alone, by the server's
/// record of each parent, and only once the worker is done: not working, asking the user nothing, with no children of
/// its own, and its pull request merged, closed or none. The root's delete script decides the rest, as for the user.
/// </summary>
public class FleetDeleteTests
{
    /// <summary>An action whose sessions start, take their prompt, and wait, mid-turn: Running.</summary>
    private const string BusyAction = "busy";

    /// <summary>An action whose sessions ask a permission, which waits on the user.</summary>
    private const string AskingAction = "asking";

    /// <summary>An action whose sessions ask the user a question with AskUserQuestion.</summary>
    private const string QuestioningAction = "questioning";

    /// <summary>In a worker's folder, the delete script refuses, as a real one does for work not pushed, unless forced.</summary>
    private const string UnpushedMarker = "unpushed";

    /// <summary>In the root, what the status script prints for every session: its pull request.</summary>
    private const string PullRequestFile = "pr.json";

    /// <summary>Where the delete script notes each folder it took down.</summary>
    private const string DeletedLog = "deleted.txt";

    private const string DeleteScript = """
        $ErrorActionPreference = 'Stop'
        if ($env:GODMODE_FORCE -ne 'true' -and (Test-Path (Join-Path $env:GODMODE_PROJECT_PATH 'unpushed'))) {
            [Console]::Error.WriteLine("The worktree has work not pushed: $env:GODMODE_PROJECT_FOLDER stays")
            exit 1
        }
        Add-Content -Path (Join-Path $env:GODMODE_ROOT_PATH 'deleted.txt') -Value $env:GODMODE_PROJECT_FOLDER
        """;

    private const string StatusScript = """
        $ErrorActionPreference = 'Stop'
        $pr = Join-Path $env:GODMODE_ROOT_PATH 'pr.json'
        if (Test-Path $pr) { Get-Content -Raw $pr } else { '{}' }
        """;

    /// <summary>The overseer: it starts, takes its prompt, and waits.</summary>
    private static FakeScript Waiting() => new FakeScript().EmitInit().AwaitStdin().AwaitStdin();

    /// <summary>A worker: its turn ends, and it is Idle.</summary>
    private static FakeScript Turns() => new FakeScript().EmitInit().AwaitStdin().EmitResult("first").AwaitStdin();

    private static readonly Dictionary<string, object?> Scripts = new() { ["delete"] = "delete.ps1", ["status"] = "status.ps1" };

    /// <summary>A run whose actions delete and report a pull request by the scripts above, an overseer in it, and its fleet client.</summary>
    private sealed class Fleet : IAsyncDisposable
    {
        public FleetRun Run { get; private init; } = null!;
        public string OverseerId { get; private init; } = "";
        public McpClient Overseer { get; private init; } = null!;
        public ConcurrentQueue<string> Deleted { get; } = new();

        public static async Task<Fleet> StartAsync()
        {
            // An open pull request is checked again every second
            var run = await FleetRun.StartAsync(Waiting(), $$"""{ "{{ProjectManager.PullRequestPollSetting}}": 1 }""");
            File.WriteAllText(Path.Combine(run.RootPath, ".godmode-root", "delete.ps1"), DeleteScript);
            File.WriteAllText(Path.Combine(run.RootPath, ".godmode-root", "status.ps1"), StatusScript);
            run.WriteActionScript(WorkAction, Turns(), Scripts);
            run.WriteActionScript(BusyAction, Waiting(), Scripts);
            run.WriteActionScript(AskingAction, new FakeScript().EmitInit().AwaitStdin().AskPermission("Bash", new { command = "ls" }).AwaitStdin(), Scripts);
            var question = new { questions = new[] { new { question = "Which branch?", header = "Branch", options = new[] { new { label = "main", description = "the default" } }, multiSelect = false } } };
            run.WriteActionScript(QuestioningAction, new FakeScript().EmitInit().AwaitStdin().AskPermission("AskUserQuestion", question, "toolu_ask").AwaitStdin(), Scripts);
            var (overseerId, overseer) = await OverseerAsync(run, "overseer");
            var fleet = new Fleet { Run = run, OverseerId = overseerId, Overseer = overseer };
            run.Client.Hub.On<string>(nameof(IProjectHubClient.ProjectDeleted), fleet.Deleted.Enqueue);
            return fleet;
        }

        /// <summary>An overseer action's session, created in the app, with its fleet client.</summary>
        public static async Task<(string Id, McpClient Fleet)> OverseerAsync(FleetRun run, string name)
        {
            var id = await run.CreateOverHubAsync(name, OverseerAction);
            return (id, await ConnectAsync(GodModeMcpEntry.FleetOf(await run.WaitForLaunchAsync(id, launch => launch.Stdin.Count > 0))));
        }

        /// <summary>A child of <paramref name="by"/>'s caller, of <paramref name="action"/>, under <paramref name="parent"/> when given.</summary>
        public async Task<string> StartAsync(McpClient by, string name, string action = WorkAction, string? parent = null) =>
            (await Run.StartSessionAsync(by, name, action, parent == null ? null : new() { ["parent"] = parent })).GetProperty("Id").GetString()!;

        /// <summary>A work action's child, its first turn ended: Idle, its status checked.</summary>
        public async Task<string> IdleChildAsync(McpClient by, string name, string? parent = null)
        {
            var id = await StartAsync(by, name, parent: parent);
            await Run.Client.WaitForAsync(id, s => s is { State: ProjectState.Idle, LastResult: "first" }, Run.Server);
            return id;
        }

        public string FolderOf(string id) => ServerProcess.WorkingFolderOf(Run.RootPath, id);

        public Task<string> RefusedAsync(string id, McpClient? by = null) =>
            Run.RefusedAsync(by ?? Overseer, "delete_session", new() { ["session"] = id });

        public Task<JsonElement> DeleteAsync(string id) => Run.CallAsync(Overseer, "delete_session", new() { ["session"] = id });

        public async Task<string[]> IdsAsync() =>
            (await Run.Client.Hub.InvokeAsync<ProjectSummary[]>(nameof(IProjectHub.ListProjects))).Select(s => s.Id).ToArray();

        /// <summary>The folders the delete script took down.</summary>
        public string[] ScriptDeleted()
        {
            var path = Path.Combine(Run.RootPath, DeletedLog);
            return File.Exists(path) ? File.ReadAllLines(path).Where(line => line.Length > 0).ToArray() : [];
        }

        public async ValueTask DisposeAsync()
        {
            await Overseer.DisposeAsync();
            await Run.DisposeAsync();
        }
    }

    [Fact]
    public async Task AnOverseer_DeletesItsFinishedChild_TheDeleteScriptRuns_AndItsFolderGoes_AndTheAppIsTold()
    {
        await using var fleet = await Fleet.StartAsync();
        var child = await fleet.IdleChildAsync(fleet.Overseer, "worker");
        var folder = fleet.FolderOf(child);

        var result = await fleet.DeleteAsync(child);

        Assert.False(result.GetProperty("Trashed").GetBoolean());
        Assert.Equal([Path.GetFileName(folder)], fleet.ScriptDeleted());
        Assert.False(Directory.Exists(folder), $"{folder} is still there");
        Assert.DoesNotContain(child, await fleet.IdsAsync());
        Assert.True(await LifecycleHarness.WaitForAsync(() => Task.FromResult(fleet.Deleted.Contains(child))), "ProjectDeleted was not pushed");
        // The overseer is left as it was
        Assert.Contains(fleet.OverseerId, await fleet.IdsAsync());
    }

    /// <summary>A grandchild is the overseer's too: its child's child, by the server's record of each parent.</summary>
    [Fact]
    public async Task AnOverseer_DeletesItsGrandchild_ButNotAChildThatStillHasChildren()
    {
        await using var fleet = await Fleet.StartAsync();
        var child = await fleet.IdleChildAsync(fleet.Overseer, "worker");
        var grandchild = await fleet.IdleChildAsync(fleet.Overseer, "helper", parent: child);

        var refused = await fleet.RefusedAsync(child);
        Assert.Contains("of its own", refused);
        Assert.Contains(grandchild, refused);
        Assert.Contains(child, await fleet.IdsAsync());
        Assert.Empty(fleet.ScriptDeleted());

        await fleet.DeleteAsync(grandchild);
        await fleet.DeleteAsync(child);
        Assert.Equal([fleet.OverseerId], await fleet.IdsAsync());
    }

    /// <summary>
    /// Another overseer's child is not the caller's, though it is in the caller's profile and root, and nor is a session
    /// the user started; the server's credential, the user's own overseer, has no children to delete.
    /// </summary>
    [Fact]
    public async Task ASiblingsChild_ATopLevelSession_AndTheServersCredential_AreRefused_AndNothingIsDeleted()
    {
        await using var fleet = await Fleet.StartAsync();
        var (_, sibling) = await Fleet.OverseerAsync(fleet.Run, "sibling");
        await using var _ = sibling;
        var siblingsChild = await fleet.IdleChildAsync(sibling, "theirs");
        var usersSession = await fleet.Run.CreateOverHubAsync("users", WorkAction);
        await fleet.Run.Client.WaitForAsync(usersSession, s => s.State == ProjectState.Idle, fleet.Run.Server);

        Assert.Contains("not one you started", await fleet.RefusedAsync(siblingsChild));
        Assert.Contains("not one you started", await fleet.RefusedAsync(usersSession));
        await using var user = await fleet.Run.ConnectFleetAsync();
        Assert.Contains("Only a GodMode session", await fleet.RefusedAsync(siblingsChild, user));

        Assert.Contains(siblingsChild, await fleet.IdsAsync());
        Assert.Contains(usersSession, await fleet.IdsAsync());
        Assert.Empty(fleet.ScriptDeleted());
    }

    [Fact]
    public async Task AnOverseer_DoesNotDeleteItself()
    {
        await using var fleet = await Fleet.StartAsync();

        Assert.Contains("does not delete itself", await fleet.RefusedAsync(fleet.OverseerId));
        Assert.Contains(fleet.OverseerId, await fleet.IdsAsync());
    }

    /// <summary>A child mid-turn, or waiting on the user's answer to a permission prompt or a question, is not stopped by a delete: it is refused.</summary>
    [Fact]
    public async Task ARunningChild_AndOneWaitingOnAPermissionOrAQuestion_AreRefused_AndKeepTheirState()
    {
        await using var fleet = await Fleet.StartAsync();
        var running = await fleet.StartAsync(fleet.Overseer, "running", BusyAction);
        await fleet.Run.Client.WaitForAsync(running, s => s.State == ProjectState.Running, fleet.Run.Server);
        var asking = await fleet.StartAsync(fleet.Overseer, "asking", AskingAction);
        await fleet.Run.Client.WaitForAsync(asking, s => s.State == ProjectState.WaitingPermission, fleet.Run.Server);

        var questioning = await fleet.StartAsync(fleet.Overseer, "questioning", QuestioningAction);
        await fleet.Run.Client.WaitForAsync(questioning, s => s.PendingQuestion != null, fleet.Run.Server);

        Assert.Contains("running", await fleet.RefusedAsync(running));
        Assert.Contains("permission", await fleet.RefusedAsync(asking));
        Assert.Contains("question", await fleet.RefusedAsync(questioning));

        Assert.Equal(ProjectState.Running, (await fleet.Run.Client.Hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.GetStatus), running)).State);
        Assert.Equal(ProjectState.WaitingPermission, (await fleet.Run.Client.Hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.GetStatus), asking)).State);
        Assert.NotNull((await fleet.Run.Client.Hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.GetStatus), questioning)).PendingQuestion);
        Assert.Empty(fleet.ScriptDeleted());
    }

    /// <summary>Never forced: the root's delete script refuses work not pushed (which it would let go if forced), its error is the caller's, and the session stays.</summary>
    [Fact]
    public async Task WhenTheDeleteScriptFails_ItsErrorIsTheCallers_AndTheChildStays()
    {
        await using var fleet = await Fleet.StartAsync();
        var child = await fleet.IdleChildAsync(fleet.Overseer, "worker");
        var folder = fleet.FolderOf(child);
        File.WriteAllText(Path.Combine(folder, UnpushedMarker), "");

        Assert.Contains("work not pushed", await fleet.RefusedAsync(child));

        Assert.True(Directory.Exists(folder));
        Assert.Contains(child, await fleet.IdsAsync());
        Assert.DoesNotContain(child, fleet.Deleted);

        // Pushed, it goes
        File.Delete(Path.Combine(folder, UnpushedMarker));
        await fleet.DeleteAsync(child);
        Assert.DoesNotContain(child, await fleet.IdsAsync());
    }

    /// <summary>A child whose pull request is still draft or open is refused; merged (or closed) it goes.</summary>
    [Fact]
    public async Task AChildWithAnOpenPullRequest_IsRefused_UntilItIsMerged()
    {
        await using var fleet = await Fleet.StartAsync();
        void Report(string state) => File.WriteAllText(Path.Combine(fleet.Run.RootPath, PullRequestFile),
            $$$"""{"pullRequest": {"url": "https://github.com/o/r/pull/12", "number": 12, "state": "{{{state}}}", "review": "none"}}""");
        Task<ProjectStatus> ReportedAsync(string id, PullRequestState state) =>
            fleet.Run.Client.WaitForAsync(id, s => s.PullRequest?.State == state, fleet.Run.Server);

        Report("open");
        var child = await fleet.IdleChildAsync(fleet.Overseer, "worker");
        await ReportedAsync(child, PullRequestState.Open);

        var refused = await fleet.RefusedAsync(child);
        Assert.Contains("https://github.com/o/r/pull/12", refused);
        Assert.Contains("merged or closed", refused);
        Assert.Contains(child, await fleet.IdsAsync());
        Assert.Empty(fleet.ScriptDeleted());

        Report("merged");
        await ReportedAsync(child, PullRequestState.Merged);
        await fleet.DeleteAsync(child);
        Assert.DoesNotContain(child, await fleet.IdsAsync());
    }
}
