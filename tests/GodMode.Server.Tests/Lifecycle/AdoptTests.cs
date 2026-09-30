using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Models;
using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using SessionState = GodMode.ProjectFiles.SessionState;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// Adopting folders that exist in a root (#370). Two fixture roots: one with a <c>list</c> script, an
/// action that adopts (<c>"adopt": true</c>) and one that does not, whose create script would make a
/// worktree; and one with neither, whose immediate subfolders are offered. A folder a session is in is
/// never offered or adopted; an adopt runs no script but the adopt action's, makes nothing, and starts the
/// session idle; Forget runs no delete script and keeps the folder, and its undo brings the session back.
/// </summary>
public sealed class AdoptTests
{
    /// <summary>Offers three folders; one of them has a session, which the server leaves out.</summary>
    private const string ListScript = """
        $ErrorActionPreference = 'Stop'
        @(
            [ordered]@{ path = 'feature-12'; name = 'Fix the list'; kind = 'feat'; action = 'issue'; inputs = [ordered]@{ issue = 12; branch = 'feature/12-fix' } },
            [ordered]@{ path = (Join-Path $env:GODMODE_ROOT_PATH 'has-session'); name = 'Has one' },
            [ordered]@{ path = 'plain-one' }
        ) | ConvertTo-Json -Depth 5 -AsArray
        """;

    /// <summary>
    /// The adopt action's create script: says what it saw, and names the session. Were it run as a create
    /// (no GODMODE_ADOPT), it would make a worktree.
    /// </summary>
    private const string AdoptScript = """
        $ErrorActionPreference = 'Stop'
        Set-Content -Path (Join-Path $env:GODMODE_ROOT_PATH 'logs/adopt-seen.txt') -Value "adopt=[$env:GODMODE_ADOPT] path=[$env:GODMODE_PROJECT_PATH] branch=[$env:GODMODE_INPUT_BRANCH]"
        if ($env:GODMODE_ADOPT -ne 'true') { New-Item -ItemType Directory -Force (Join-Path $env:GODMODE_ROOT_PATH 'worktree-made') | Out-Null }
        Set-Content -Path $env:GODMODE_RESULT_FILE -Value "project_name=Issue $env:GODMODE_INPUT_ISSUE`nkind=feat`nproject_path=$env:GODMODE_ROOT_PATH"
        """;

    /// <summary>A create script that knows nothing of adopting: it makes a worktree. An adopt must never run it.</summary>
    private const string MakesAWorktree = """
        $ErrorActionPreference = 'Stop'
        New-Item -ItemType Directory -Force (Join-Path $env:GODMODE_ROOT_PATH 'worktree-made') | Out-Null
        Set-Content -Path (Join-Path $env:GODMODE_ROOT_PATH 'logs/plain-ran.txt') -Value 'ran'
        """;

    /// <summary>The root's delete script: says it ran. A forget must never run it.</summary>
    private const string DeleteScript = """
        $ErrorActionPreference = 'Stop'
        Set-Content -Path (Join-Path $env:GODMODE_ROOT_PATH 'logs/delete-ran.txt') -Value $env:GODMODE_SESSION_ID
        """;

    private static FakeScript WaitsForItsFirstMessage() => new FakeScript().AwaitStdin().EmitInit().EmitAssistant("Done.").EmitResult();

    /// <summary>The root with a list script, an adopt action (<c>issue</c>) and one that is not (<c>plain</c>), and a delete script.</summary>
    private static LifecycleHarness ListingRoot(IReadOnlyDictionary<string, string?>? settings = null)
    {
        var harness = new LifecycleHarness(WaitsForItsFirstMessage(),
            rootConfig: new Dictionary<string, object> { ["list"] = "list.ps1", ["delete"] = "delete.ps1" }, settings: settings);
        var config = Path.Combine(harness.RootPath, ".godmode-root");
        File.WriteAllText(Path.Combine(config, "list.ps1"), ListScript);
        File.WriteAllText(Path.Combine(config, "delete.ps1"), DeleteScript);
        File.WriteAllText(Path.Combine(config, "config.issue.json"), """{ "adopt": true, "create": "issue/create.ps1" }""");
        Directory.CreateDirectory(Path.Combine(config, "issue"));
        File.WriteAllText(Path.Combine(config, "issue", "create.ps1"), AdoptScript);
        File.WriteAllText(Path.Combine(config, "config.plain.json"), """{ "create": "plain/create.ps1" }""");
        Directory.CreateDirectory(Path.Combine(config, "plain"));
        File.WriteAllText(Path.Combine(config, "plain", "create.ps1"), MakesAWorktree);
        Directory.CreateDirectory(Path.Combine(harness.RootPath, "logs"));
        foreach (var folder in new[] { "feature-12", "has-session", "plain-one", "not-listed" })
        {
            Directory.CreateDirectory(Path.Combine(harness.RootPath, folder));
            File.WriteAllText(Path.Combine(harness.RootPath, folder, "work.txt"), $"the work in {folder}");
        }
        LifecycleHarness.PlantSession(Path.Combine(harness.RootPath, "has-session"), status: PlantedStatus());
        return harness;
    }

    private static Shared.Models.ProjectStatus PlantedStatus() => new(LifecycleHarness.PlantedId(), "planted", ProjectState.Stopped,
        DateTime.UtcNow, DateTime.UtcNow, null, new Shared.Models.ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0);

    private static string Logged(LifecycleHarness harness, string file) => Path.Combine(harness.RootPath, "logs", file);

    private static void AssertNothingWasMade(LifecycleHarness harness)
    {
        Assert.False(Directory.Exists(Path.Combine(harness.RootPath, "worktree-made")), "a worktree was made");
        Assert.False(File.Exists(Logged(harness, "plain-ran.txt")), "the create script of an action that does not adopt ran");
    }

    [Fact]
    public async Task ListScript_GivesItsCandidates_LeavingOutFoldersWithASession()
    {
        await using var harness = ListingRoot();
        await harness.Projects.RecoverProjectsAsync();

        var listed = await harness.Projects.ListUnmanagedAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName);

        Assert.Equal(["feature-12", "plain-one"], listed.Select(folder => folder.Path));
        var feature = listed[0];
        Assert.Equal(("Fix the list", "feat", "issue"), (feature.Name, feature.Kind, feature.ActionName));
        Assert.Equal(12, feature.Inputs!["issue"].GetInt32());
        Assert.Equal("feature/12-fix", feature.Inputs["branch"].GetString());
        var plain = listed[1];
        Assert.Equal(("plain-one", null, null, null), (plain.Name, plain.Kind, plain.ActionName, plain.Inputs));

        // Adopted, it is a session's: it is not offered again, tracked or after a restart
        await harness.AdoptAsync("feature-12", "issue", new Dictionary<string, object> { ["issue"] = 12 });
        Assert.Equal(["plain-one"], (await harness.Projects.ListUnmanagedAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName)).Select(f => f.Path));
        await harness.RestartAsync();
        Assert.Equal(["plain-one"], (await harness.Projects.ListUnmanagedAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName)).Select(f => f.Path));
    }

    [Fact]
    public async Task WithNoListScript_TheImmediateSubfoldersAreOffered_ButTheRootsOwn_HiddenOnes_AndThoseWithASession()
    {
        await using var harness = new LifecycleHarness(WaitsForItsFirstMessage());
        foreach (var folder in new[] { "b-folder", "a-folder", ".hidden", "logs", "with-session" })
            Directory.CreateDirectory(Path.Combine(harness.RootPath, folder));
        LifecycleHarness.PlantSession(Path.Combine(harness.RootPath, "with-session"), status: PlantedStatus());
        await harness.Projects.RecoverProjectsAsync();
        var created = await harness.CreateProjectAsync("made-here", prompt: null);

        var listed = await harness.Projects.ListUnmanagedAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName);

        Assert.Equal(["a-folder", "b-folder"], listed.Select(folder => folder.Path));
        Assert.All(listed, folder => Assert.Equal(folder.Path, folder.Name));
        Assert.DoesNotContain(listed, folder => folder.Path == Path.GetFileName(harness.ProjectPath(created.Id)));
    }

    [Fact]
    public async Task Adopt_WithAnAdoptAction_RunsItsScriptWithGodModeAdopt_MakesNothing_AndStartsIdle()
    {
        await using var harness = ListingRoot();
        await harness.Projects.RecoverProjectsAsync();
        var candidate = (await harness.Projects.ListUnmanagedAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName)).First();
        var folder = Path.Combine(harness.RootPath, "feature-12");

        var adopted = await harness.Projects.AdoptFolderAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName,
            candidate.Path, candidate.ActionName, candidate.Inputs);

        Assert.Equal($"adopt=[true] path=[{folder}] branch=[feature/12-fix]", File.ReadAllText(Logged(harness, "adopt-seen.txt")).Trim());
        AssertNothingWasMade(harness);
        Assert.Equal(("Issue 12", "feat", "issue", ProjectState.Idle), (adopted.Name, adopted.Kind, adopted.ActionName, adopted.State));
        Assert.Matches(LifecycleHarness.IdPattern("issue-12", kind: "feat"), adopted.Id);
        Assert.True(adopted.Adopted);
        // The folder the session works in is the one adopted, as it was, with its state in .godmode
        var tracked = ((ProjectManager)harness.Projects).Tracked(adopted.Id)!;
        Assert.Equal(folder, tracked.ProjectPath);
        Assert.Equal("the work in feature-12", File.ReadAllText(Path.Combine(folder, "work.txt")));
        Assert.True(File.Exists(Path.Combine(SessionState.PathOf(folder, tracked.SessionId), "status.json")));
        Assert.Contains(await harness.Projects.ListProjectsAsync(), p => p.Id == adopted.Id && p.Adopted && p.State == ProjectState.Idle);
        var launch = await harness.WaitForLaunchAsync(adopted.Id, _ => true);
        Assert.True(LifecycleHarness.IsProcessAlive(launch.Pid), "claude should be waiting for its first message");

        // Adopted it stays, across a restart
        await harness.RestartAsync();
        Assert.Contains(await harness.Projects.ListProjectsAsync(), p => p.Id == adopted.Id && p.Adopted);
    }

    [Fact]
    public async Task Adopt_WithAnActionThatDoesNotAdopt_RunsNoScript()
    {
        await using var harness = ListingRoot();
        await harness.Projects.RecoverProjectsAsync();

        var adopted = await harness.AdoptAsync("plain-one", "plain");

        AssertNothingWasMade(harness);
        Assert.False(File.Exists(Logged(harness, "adopt-seen.txt")), "the adopt action's script ran for another action");
        Assert.Equal(("plain-one", ProjectState.Idle), (adopted.Name, adopted.State));
        Assert.True(adopted.Adopted);
        Assert.Equal(Path.Combine(harness.RootPath, "plain-one"), harness.ProjectPath(adopted.Id));
        Assert.Equal("the work in plain-one", File.ReadAllText(Path.Combine(harness.RootPath, "plain-one", "work.txt")));
    }

    [Fact]
    public async Task Adopt_WithNoScriptAtAll_NamesTheSessionAfterItsInputs_OrItsFolder()
    {
        await using var harness = new LifecycleHarness(WaitsForItsFirstMessage());
        Directory.CreateDirectory(Path.Combine(harness.RootPath, "old-share"));
        Directory.CreateDirectory(Path.Combine(harness.RootPath, "other"));
        await harness.Projects.RecoverProjectsAsync();

        var named = await harness.AdoptAsync("old-share", inputs: new Dictionary<string, object> { ["name"] = "The old share", ["kind"] = "share" });
        var plain = await harness.AdoptAsync("other");

        Assert.Equal(("The old share", "share"), (named.Name, named.Kind));
        Assert.Equal(("other", "create"), (plain.Name, plain.Kind));
        Assert.All([named, plain], status => Assert.Equal(ProjectState.Idle, status.State));
    }

    [Fact]
    public async Task Adopt_WithABrokenConfig_IsRefused_AndRunsNothing()
    {
        await using var harness = ListingRoot();
        await harness.Projects.RecoverProjectsAsync();
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "config.issue.json"), """{ "adopt": true, """);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => harness.AdoptAsync("plain-one", "plain"));

        Assert.Contains("cannot be read", refused.Message);
        AssertNothingWasMade(harness);
        Assert.False(Directory.Exists(Path.Combine(harness.RootPath, "plain-one", ".godmode")), "the refused adopt wrote state");
        Assert.DoesNotContain(await harness.Projects.ListProjectsAsync(), p => p.Adopted);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("OUTSIDE")]
    [InlineData("feature-12/nested")]
    [InlineData("logs")]
    [InlineData(".godmode-root")]
    [InlineData("missing")]
    public async Task Adopt_OfAPathThatIsNoFolderDirectlyInTheRoot_IsRefused(string path)
    {
        await using var harness = ListingRoot();
        var outside = Path.Combine(harness.RootsDir, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(harness.RootPath, "feature-12", "nested"));
        await harness.Projects.RecoverProjectsAsync();
        if (path == "OUTSIDE") path = outside;

        await Assert.ThrowsAsync<ArgumentException>(() => harness.AdoptAsync(path, "plain"));

        AssertNothingWasMade(harness);
        Assert.False(Directory.Exists(Path.Combine(outside, ".godmode")));
        Assert.False(Directory.Exists(Path.Combine(harness.RootPath, "feature-12", "nested", ".godmode")));
        Assert.False(Directory.Exists(Path.Combine(harness.RootPath, "missing")), "an adopt made a folder");
        Assert.DoesNotContain(await harness.Projects.ListProjectsAsync(), p => p.Adopted);
    }

    [Fact]
    public async Task Adopt_OfAFolderASessionIsIn_IsRefused()
    {
        await using var harness = ListingRoot();
        await harness.Projects.RecoverProjectsAsync();
        var first = await harness.AdoptAsync("plain-one", "plain");

        await Assert.ThrowsAsync<ProjectInUseException>(() => harness.AdoptAsync("plain-one", "plain"));
        // The planted session is recovered, and tracked; on disk only, it would be refused as well
        await Assert.ThrowsAsync<ProjectInUseException>(() => harness.AdoptAsync("has-session", "plain"));

        Assert.Equal([first.Id], (await harness.Projects.ListProjectsAsync()).Where(p => p.Adopted).Select(p => p.Id));
    }

    [Fact]
    public async Task Forget_KeepsTheFolder_RunsNoDeleteScript_AndItsUndoBringsTheSessionBackAsItWas()
    {
        await using var harness = ListingRoot();
        await harness.Projects.RecoverProjectsAsync();
        var client = harness.Connect("c1");
        var adopted = await client.AdoptFolderAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName, "plain-one", "plain", null);
        Assert.Contains(harness.Hub.Pushes, push => push.Method == nameof(IProjectHubClient.ProjectCreated));
        var folder = Path.Combine(harness.RootPath, "plain-one");
        await harness.WaitForLaunchAsync(adopted.Id, _ => true);

        var forgotten = await client.ForgetProjectAsync(adopted.Id);

        Assert.True(forgotten.Trashed);
        Assert.False(File.Exists(Logged(harness, "delete-ran.txt")), "a forget ran the root's delete script");
        Assert.Equal("the work in plain-one", File.ReadAllText(Path.Combine(folder, "work.txt")));
        Assert.DoesNotContain(await harness.Projects.ListProjectsAsync(), p => p.Id == adopted.Id);
        Assert.Contains(harness.Hub.Pushes, push => push.Method == nameof(IProjectHubClient.ProjectDeleted));
        // The folder is no session's now: it is offered again
        Assert.Contains(await harness.Projects.ListUnmanagedAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName), f => f.Path == "plain-one");

        var restored = await harness.Projects.RestoreProjectAsync(adopted.Id);

        Assert.Equal(adopted.Id, restored.Id);
        Assert.True(restored.Adopted);
        Assert.False(restored.SharedFolder);
        // Back as it was, owning its folder: its delete follows the root's rules, script and folder
        await harness.Projects.DeleteProjectAsync(adopted.Id);
        Assert.True(File.Exists(Logged(harness, "delete-ran.txt")));
        Assert.False(Directory.Exists(folder), "the restored session's delete should remove its folder, as a worktree's does");
    }

    [Fact]
    public async Task Forget_OfASessionItDidNotAdopt_KeepsItsFolderToo()
    {
        await using var harness = new LifecycleHarness(WaitsForItsFirstMessage(),
            rootConfig: new Dictionary<string, object> { ["delete"] = "delete.ps1" });
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "delete.ps1"), DeleteScript);
        await harness.Projects.RecoverProjectsAsync();
        var created = await harness.CreateProjectAsync("made-here", prompt: null);
        var folder = harness.ProjectPath(created.Id);

        Assert.True((await harness.Projects.ForgetProjectAsync(created.Id)).Trashed);

        Assert.True(Directory.Exists(folder));
        Assert.False(File.Exists(Logged(harness, "delete-ran.txt")));
        Assert.Contains(await harness.Projects.ListUnmanagedAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName), f => f.Path == Path.GetFileName(folder));
    }

    [Theory]
    [InlineData("""Write-Output '{"path": "feature-12"}'""", "not an array")]
    [InlineData("""Write-Output '[{"path": "../elsewhere"}]'""", "not a folder directly in its root")]
    [InlineData("""Write-Output '[{"path": "feature-12", "colour": "red"}]'""", "unknown property 'colour'")]
    [InlineData("""Write-Output '[{"path": "feature-12", "action": "nope"}]'""", "action 'nope'")]
    [InlineData("""Write-Output '[{"path": "feature-12"}, {"path": "feature-12"}]'""", "listed twice")]
    [InlineData("""Write-Output '[{"name": "no path"}]'""", "has no path")]
    [InlineData("throw 'no list today'", "failed")]
    [InlineData("Start-Sleep -Seconds 20", "took longer")]
    public async Task AListScriptThatPrintsAnythingButTheList_FailsTheListing_SayingWhy(string script, string reason)
    {
        await using var harness = ListingRoot(new Dictionary<string, string?> { [ProjectManager.ListScriptTimeoutSetting] = "3" });
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "list.ps1"), $"$ErrorActionPreference = 'Stop'\n{script}\n");
        await harness.Projects.RecoverProjectsAsync();

        var failed = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() =>
            harness.Connect("c1").ListUnmanagedAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName));

        Assert.Contains(reason, failed.Message);
    }

    [Fact]
    public async Task AListScriptThatPrintsNothing_OffersNothing()
    {
        await using var harness = ListingRoot();
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "list.ps1"), "$ErrorActionPreference = 'Stop'\n");
        await harness.Projects.RecoverProjectsAsync();

        Assert.Empty(await harness.Projects.ListUnmanagedAsync(LifecycleHarness.ProfileName, LifecycleHarness.RootName));
    }
}
