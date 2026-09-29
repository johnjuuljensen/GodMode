using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Models;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using SessionState = GodMode.ProjectFiles.SessionState;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// An action with <c>sharedFolder</c> (an assistant's workspace) runs several sessions in one working
/// folder, each with its own state, output, MCP token, create log and claude. A delete of one removes
/// only its <c>.godmode/sessions/{id}/</c>, never the folder or its files. Without <c>sharedFolder</c> a
/// folder another session uses is refused, and a shared session may join only sessions that share too.
/// </summary>
public class SharedFolderTests
{
    private static FakeScript Waiting() => new FakeScript().EmitInit().AwaitStdin();

    private static readonly Dictionary<string, object> Shared = new() { ["sharedFolder"] = true };

    /// <summary>Two sessions named alike: one folder, as a shared action's are, and two ids.</summary>
    private static async Task<(ProjectStatus First, ProjectStatus Second)> TwoInOneFolderAsync(LifecycleHarness harness)
    {
        var first = await harness.CreateProjectAsync("assistant", "first");
        var second = await harness.CreateProjectAsync("assistant", "second");
        await harness.WaitForStdinAsync(first.Id);
        await harness.WaitForStdinAsync(second.Id);
        return (first, second);
    }

    /// <summary>
    /// Both sessions run in the one folder, claude's working directory, and each has its own state
    /// folder, output, and MCP config with its own ID and token, which is no good for the other.
    /// </summary>
    [Fact]
    public async Task TwoSessions_InOneSharedFolder_EachHaveTheirOwnStateOutputAndToken()
    {
        await using var harness = new LifecycleHarness(Waiting(), rootConfig: Shared);

        var (first, second) = await TwoInOneFolderAsync(harness);

        Assert.NotEqual(first.Id, second.Id);
        var folder = harness.ProjectPath(first.Id);
        Assert.Equal(folder, harness.ProjectPath(second.Id));
        Assert.NotEqual(harness.StatePath(first.Id), harness.StatePath(second.Id));
        Assert.Equal(new[] { first.Id.Split('/')[^1], second.Id.Split('/')[^1] }.Order(StringComparer.Ordinal), SessionState.List(folder));

        var firstLaunch = Assert.Single(harness.Launches(first.Id));
        var secondLaunch = Assert.Single(harness.Launches(second.Id));
        Assert.NotEqual(firstLaunch.Pid, secondLaunch.Pid);
        Assert.Contains("first", Assert.Single(firstLaunch.Stdin));
        Assert.Contains("second", Assert.Single(secondLaunch.Stdin));

        // Each session's MCP config is in its own state folder, for its own ID, with its own token
        Assert.StartsWith(harness.StatePath(first.Id), firstLaunch.ArgValue("--mcp-config"));
        Assert.StartsWith(harness.StatePath(second.Id), secondLaunch.ArgValue("--mcp-config"));
        var (firstMcp, secondMcp) = (GodModeMcpEntry.Parse(ReadMcpConfig(harness, first.Id)), GodModeMcpEntry.Parse(ReadMcpConfig(harness, second.Id)));
        Assert.Equal((first.Id, second.Id), (firstMcp.ProjectId, secondMcp.ProjectId));
        Assert.NotEqual(firstMcp.Token, secondMcp.Token);
        Assert.NotNull(harness.Projects.ValidateProjectToken(first.Id, firstMcp.Token));
        Assert.NotNull(harness.Projects.ValidateProjectToken(second.Id, secondMcp.Token));
        Assert.Null(harness.Projects.ValidateProjectToken(second.Id, firstMcp.Token));

        // Each session's output is its own. Its init line reaches output.jsonl through the pipeline,
        // which can be after claude has had its stdin
        Assert.NotEqual(firstLaunch.ArgValue("--session-id"), secondLaunch.ArgValue("--session-id"));
        foreach (var (id, launch) in new[] { (first.Id, firstLaunch), (second.Id, secondLaunch) })
            await LifecycleHarness.WaitUntilAsync(() => Task.FromResult(harness.ReadOutputFile(id).Contains(launch.ArgValue("--session-id")!)), null,
                () => $"{id}'s output has no init line:\n{harness.ReadOutputFile(id)}");
        Assert.Contains(firstLaunch.ArgValue("--session-id")!, harness.ReadOutputFile(first.Id));
        Assert.DoesNotContain(firstLaunch.ArgValue("--session-id")!, harness.ReadOutputFile(second.Id));
        Assert.Contains(secondLaunch.ArgValue("--session-id")!, harness.ReadOutputFile(second.Id));
    }

    /// <summary>A restart recovers both sessions of the folder, each with its own claude session.</summary>
    [Fact]
    public async Task Restart_RecoversBothSessionsOfTheFolder()
    {
        await using var harness = new LifecycleHarness(Waiting(), rootConfig: Shared);
        var (first, second) = await TwoInOneFolderAsync(harness);
        var claudeSessions = (harness.Launches(first.Id)[0].ArgValue("--session-id"), harness.Launches(second.Id)[0].ArgValue("--session-id"));

        await harness.RestartAsync(resume: false);

        Assert.Equal(new[] { first.Id, second.Id }.Order(StringComparer.Ordinal),
            (await harness.Projects.ListProjectsAsync()).Select(p => p.Id).Order(StringComparer.Ordinal));
        Assert.Equal(claudeSessions, (harness.Tracked(first.Id).ClaudeSessionId, harness.Tracked(second.Id).ClaudeSessionId));
        Assert.True(harness.Tracked(first.Id).SharedFolder);
        Assert.True(harness.Tracked(second.Id).SharedFolder);
    }

    /// <summary>
    /// Deleting one session removes its state folder, and only that: the other session runs on with
    /// its state, and the folder and its files stay. The delete script is told the session's id and
    /// that the folder is shared.
    /// </summary>
    [Fact]
    public async Task DeleteOfOne_LeavesTheOtherAndTheFolder()
    {
        await using var harness = new LifecycleHarness(Waiting(),
            rootConfig: new Dictionary<string, object>(Shared) { ["delete"] = "delete.ps1" });
        var told = Path.Combine(harness.WorkDir, "told.txt");
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "delete.ps1"),
            $"Set-Content -Path '{told}' -Value \"$env:GODMODE_SESSION_ID $env:GODMODE_SHARED_FOLDER\"");
        var (first, second) = await TwoInOneFolderAsync(harness);
        var folder = harness.ProjectPath(first.Id);
        var notes = Path.Combine(folder, "notes.md");
        File.WriteAllText(notes, "the workspace's own");
        var secondLaunch = Assert.Single(harness.Launches(second.Id));

        await harness.Projects.DeleteProjectAsync(first.Id);

        Assert.Equal($"{first.Id.Split('/')[^1]} true", File.ReadAllText(told).Trim());
        Assert.False(Directory.Exists(harness.StatePath(first.Id)), "the deleted session's state is still there");
        Assert.True(File.Exists(notes), "the folder's files went with the session");
        Assert.True(Directory.Exists(harness.StatePath(second.Id)), "the other session's state went too");
        Assert.Equal(second.Id, Assert.Single(await harness.Projects.ListProjectsAsync()).Id);
        Assert.True(LifecycleHarness.IsProcessAlive(secondLaunch.Pid), "the other session's claude was stopped");
        Assert.Equal(ProjectState.Running, (await harness.Projects.GetStatusAsync(second.Id)).State);

        // The last one leaves the folder too
        await harness.Projects.DeleteProjectAsync(second.Id);
        Assert.True(File.Exists(notes), "the last session's delete took the folder");
        Assert.Empty(SessionState.List(folder));

        await harness.RestartAsync(resume: false);
        Assert.Empty(await harness.Projects.ListProjectsAsync());
    }

    /// <summary>
    /// A session created shared stays shared: its delete leaves the folder even once its root's config
    /// no longer shares folders and it is the folder's last session.
    /// </summary>
    [Fact]
    public async Task Delete_OfASessionCreatedShared_LeavesTheFolder_AfterTheConfigStopsSharing()
    {
        await using var harness = new LifecycleHarness(Waiting(), rootConfig: Shared);
        var created = await harness.CreateProjectAsync("assistant");
        await harness.WaitForStdinAsync(created.Id);
        var folder = harness.ProjectPath(created.Id);
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "config.json"),
            File.ReadAllText(Path.Combine(harness.RootPath, ".godmode-root", "config.json")).Replace("\"sharedFolder\":true", "\"sharedFolder\":false"));
        Assert.DoesNotContain("\"sharedFolder\":true", File.ReadAllText(Path.Combine(harness.RootPath, ".godmode-root", "config.json")));

        await harness.Projects.DeleteProjectAsync(created.Id);

        Assert.True(Directory.Exists(folder), "the shared folder was deleted");
        Assert.False(Directory.Exists(harness.StatePath(created.Id)));
    }

    /// <summary>
    /// A recovered session whose settings.json cannot be read is taken as shared: with its action no
    /// longer sharing, and no other session in the folder, its delete still removes only its state,
    /// never the workspace and its files.
    /// </summary>
    [Fact]
    public async Task Delete_OfASessionWhoseSettingsCannotBeRead_LeavesTheFolder()
    {
        await using var harness = new LifecycleHarness(Waiting(), rootConfig: Shared);
        var created = await harness.CreateProjectAsync("assistant");
        await harness.WaitForStdinAsync(created.Id);
        var folder = harness.ProjectPath(created.Id);
        var notes = Path.Combine(folder, "notes.md");
        File.WriteAllText(notes, "the workspace's own");
        File.WriteAllText(Path.Combine(harness.StatePath(created.Id), "settings.json"), "{ not json");
        var config = Path.Combine(harness.RootPath, ".godmode-root", "config.json");
        File.WriteAllText(config, File.ReadAllText(config).Replace("\"sharedFolder\":true", "\"sharedFolder\":false"));
        await harness.RestartAsync(resume: false);
        Assert.True(harness.Tracked(created.Id).SharedFolder, "a session whose settings cannot be read was taken as owning its folder");

        await harness.Projects.DeleteProjectAsync(created.Id);

        Assert.True(File.Exists(notes), "the workspace went with the session");
        Assert.False(Directory.Exists(harness.StatePath(created.Id)), "the session's state is still there");
        Assert.Contains(harness.Warnings, line => line.Contains("settings.json") && line.Contains("sharing its folder"));
    }

    /// <summary>
    /// Without sharedFolder, a create into a folder another session uses is refused, as before: the
    /// worktree root's guard against two sessions in one worktree.
    /// </summary>
    [Fact]
    public async Task CreateIntoAUsedFolder_WithoutSharedFolder_IsRefused()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var created = await harness.CreateProjectAsync("assistant");
        await harness.WaitForStdinAsync(created.Id);

        var refused = await Assert.ThrowsAsync<ProjectInUseException>(() =>
            harness.CreateProjectAsync("assistant", inputs: new Dictionary<string, object> { ["__reuseExisting"] = true }));

        Assert.Contains("is in use", refused.Message);
        Assert.Equal(created.Id, Assert.Single(await harness.Projects.ListProjectsAsync()).Id);
        Assert.Equal([created.Id.Split('/')[^1]], SessionState.List(harness.ProjectPath(created.Id)));
    }

    /// <summary>
    /// Nor does a create without sharedFolder take a folder whose only session is on disk and not
    /// tracked: its delete would remove that session's state with the folder.
    /// </summary>
    [Fact]
    public async Task CreateIntoAFolderWithAnUntrackedSession_WithoutSharedFolder_IsRefused()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var folder = Path.Combine(harness.RootPath, "assistant");
        var planted = LifecycleHarness.PlantSession(folder, status: PlantedStatus());
        // Not tracked: the server started before it was there

        var refused = await Assert.ThrowsAsync<ProjectInUseException>(() =>
            harness.CreateProjectAsync("assistant", inputs: new Dictionary<string, object> { ["__reuseExisting"] = true }));

        Assert.Contains(LifecycleHarness.PlantedSessionId, refused.Message);
        Assert.True(Directory.Exists(planted));
        Assert.Empty(await harness.Projects.ListProjectsAsync());
    }

    /// <summary>
    /// Shared and unshared do not mix in one folder: a shared create may not join a session that owns
    /// its folder (its delete would remove the folder), and an unshared one may not join shared ones.
    /// </summary>
    [Fact]
    public async Task SharedAndUnsharedSessions_DoNotMix_InOneFolder()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var godModeRoot = Path.Combine(harness.RootPath, ".godmode-root");
        File.WriteAllText(Path.Combine(godModeRoot, "config.chat.json"), """{ "sharedFolder": true }""");
        File.WriteAllText(Path.Combine(godModeRoot, "config.work.json"), "{}");

        var work = await CreateAsync(harness, "work", "owned");
        var chat = await CreateAsync(harness, "chat", "workspace");
        await harness.WaitForStdinAsync(work.Id);
        await harness.WaitForStdinAsync(chat.Id);

        var sharedIntoOwned = await Assert.ThrowsAsync<ProjectInUseException>(() => CreateAsync(harness, "chat", "owned"));
        var ownedIntoShared = await Assert.ThrowsAsync<ProjectInUseException>(() => CreateAsync(harness, "work", "workspace", reuse: true));

        Assert.Contains(work.Id, sharedIntoOwned.Message);
        Assert.Contains(chat.Id, ownedIntoShared.Message);
        Assert.Equal(2, (await harness.Projects.ListProjectsAsync()).Length);
    }

    /// <summary>
    /// Two creates into one shared folder at once both make their session, each from its own script
    /// result: the claim that keeps one session per ID is the session's, not the folder's, and so are
    /// the result file and log. The first is held in its create script, its result written and its
    /// claims made, while the second is made.
    /// </summary>
    [Fact]
    public async Task TwoCreates_IntoOneSharedFolder_AtOnce_BothMakeTheirSession()
    {
        await using var harness = new LifecycleHarness(Waiting(),
            rootConfig: new Dictionary<string, object>(Shared) { ["scriptsCreateFolder"] = true, ["create"] = "create.ps1" });
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "create.ps1"), """
            $workspace = Join-Path $env:GODMODE_ROOT_PATH 'workspace'
            New-Item -ItemType Directory -Force $workspace | Out-Null
            Set-Content -Path $env:GODMODE_RESULT_FILE -Value "project_path=$workspace`nkind=chat"
            if ($env:GODMODE_INPUT_REACHED) {
                Set-Content -Path $env:GODMODE_INPUT_REACHED -Value 'reached'
                while (-not (Test-Path $env:GODMODE_INPUT_GO)) { Start-Sleep -Milliseconds 20 }
            }
            """);
        var (reached, go) = (Path.Combine(harness.WorkDir, "reached"), Path.Combine(harness.WorkDir, "go"));
        var firstCreate = harness.CreateProjectAsync("assistant", "first", inputs: new Dictionary<string, object> { ["reached"] = reached, ["go"] = go });
        Assert.True(await LifecycleHarness.WaitForAsync(() => Task.FromResult(File.Exists(reached) || firstCreate.IsCompleted)), "the first create's script did not start");
        Assert.False(firstCreate.IsCompleted, "the first create was not held in its script");

        ProjectStatus second;
        try { second = await harness.CreateProjectAsync("assistant", "second"); }
        finally { File.WriteAllText(go, "go"); }
        var first = await firstCreate;

        Assert.NotEqual(first.Id, second.Id);
        var workspace = Path.Combine(harness.RootPath, "workspace");
        Assert.Equal((workspace, workspace), (harness.ProjectPath(first.Id), harness.ProjectPath(second.Id)));
        Assert.Equal(("chat", "chat"), (first.Kind, second.Kind));
        await harness.WaitForStdinAsync(first.Id);
        await harness.WaitForStdinAsync(second.Id);
        Assert.Equal(2, (await harness.Projects.ListProjectsAsync()).Length);
    }

    /// <summary>
    /// As an assistant root has it: a create script returns <c>{root}/workspace</c> for every session,
    /// and each session's create log and result file are its own, named by the id it keeps (here the
    /// script's kind changes it), holding what its own script wrote.
    /// </summary>
    [Fact]
    public async Task CreateLogs_OfTwoSessionsInOneFolder_AreTheirOwn()
    {
        await using var harness = new LifecycleHarness(Waiting(),
            rootConfig: new Dictionary<string, object>(Shared) { ["scriptsCreateFolder"] = true, ["create"] = "create.ps1" });
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "create.ps1"), """
            $workspace = Join-Path $env:GODMODE_ROOT_PATH 'workspace'
            New-Item -ItemType Directory -Force $workspace | Out-Null
            Write-Output "creating $env:GODMODE_SESSION_ID shared=$env:GODMODE_SHARED_FOLDER"
            Set-Content -Path $env:GODMODE_RESULT_FILE -Value "project_path=$workspace`nkind=chat"
            """);

        // One name, so one folder until the script's result: a log kept by folder would be one file
        var first = await harness.CreateProjectAsync("chat", "first");
        var second = await harness.CreateProjectAsync("chat", "second");

        Assert.Equal(Path.Combine(harness.RootPath, "workspace"), harness.ProjectPath(first.Id));
        Assert.Equal(harness.ProjectPath(first.Id), harness.ProjectPath(second.Id));
        var logs = Path.Combine(harness.RootPath, "logs");
        foreach (var created in new[] { first, second })
        {
            var id = created.Id.Split('/')[^1];
            Assert.Matches(LifecycleHarness.IdPattern("chat", kind: "chat"), created.Id);
            var log = File.ReadAllText(Path.Combine(logs, $"{id}.log"));
            // Told the id the action made, whose date and suffix the final one keeps
            // and only its own script wrote to it
            Assert.Single(log.Split('\n'), line => line.Contains("creating "));
            Assert.Matches($"creating {DateTime.Now:yyMMdd}-create-chat-{id[^4..]} shared=true", log);
            Assert.Contains("project_path=", File.ReadAllText(Path.Combine(logs, $"{id}.result")));
        }
        // Nothing is left under the ids the creates began with
        Assert.Equal(
            new[] { first, second }.SelectMany(c => new[] { $"{c.Id.Split('/')[^1]}.log", $"{c.Id.Split('/')[^1]}.result" }).Order(StringComparer.Ordinal),
            Directory.GetFiles(logs).Select(Path.GetFileName).Where(f => f!.EndsWith(".log") || f.EndsWith(".result")).Order(StringComparer.Ordinal));
    }

    private static async Task<ProjectStatus> CreateAsync(LifecycleHarness harness, string action, string name, bool reuse = false)
    {
        var inputs = new Dictionary<string, JsonElement> { ["name"] = JsonSerializer.SerializeToElement(name), ["prompt"] = JsonSerializer.SerializeToElement("hi") };
        if (reuse) inputs["__reuseExisting"] = JsonSerializer.SerializeToElement(true);
        return (await harness.Projects.CreateProjectAsync(new CreateProjectRequest(LifecycleHarness.ProfileName, LifecycleHarness.RootName, inputs, action))).Project!;
    }

    private static ProjectStatus PlantedStatus()
    {
        var now = DateTime.UtcNow;
        return new ProjectStatus(LifecycleHarness.PlantedId(), "planted", ProjectState.Stopped, now, now, null,
            new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0);
    }

    /// <summary>The MCP config while claude runs; the server replaces it whole, which a read can meet.</summary>
    private static string ReadMcpConfig(LifecycleHarness harness, string projectId) =>
        LifecycleHarness.ReadShared(Path.Combine(harness.StatePath(projectId), "mcp-config.json"));

}
