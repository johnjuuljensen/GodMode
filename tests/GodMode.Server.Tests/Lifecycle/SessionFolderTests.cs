using System.Diagnostics;
using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Shared;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using SessionState = GodMode.ProjectFiles.SessionState;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// Every session keeps its state in <c>{working folder}/.godmode/sessions/{id}/</c>, its id
/// <c>yymmdd-{kind}-{slug}-{suffix}</c>, unique within its root, and its opaque ID
/// <c>{profile}/{root}/{id}</c>. The kind is the create script's, else the action's name. A restart
/// finds sessions there, and only there: the old flat <c>.godmode/status.json</c> is no session.
/// </summary>
public class SessionFolderTests
{
    private static FakeScript Waiting() => new FakeScript().EmitInit().AwaitStdin();

    /// <summary>A create writes the session's state in <c>sessions/{id}/</c>, and nothing of it beside, the id in its form.</summary>
    [Fact]
    public async Task Create_WritesTheSessionsState_InSessionsId_WithTheIdInItsForm()
    {
        await using var harness = new LifecycleHarness(Waiting());

        var created = await harness.CreateProjectAsync("Left list");
        await harness.WaitForStdinAsync(created.Id);

        Assert.Matches(LifecycleHarness.IdPattern("left-list"), created.Id);
        var id = created.Id.Split('/')[^1];
        var godMode = Path.Combine(harness.ProjectPath(created.Id), ".godmode");
        Assert.Equal(SessionState.PathOf(harness.ProjectPath(created.Id), id), harness.StatePath(created.Id));
        // The folder's .godmode holds its .gitignore and the sessions, nothing else
        Assert.Equal([".gitignore", "sessions"], Directory.GetFileSystemEntries(godMode).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal([id], Directory.GetDirectories(Path.Combine(godMode, "sessions")).Select(Path.GetFileName));
        var state = harness.StatePath(created.Id);
        foreach (var file in new[] { "status.json", "settings.json", "input.jsonl", "output.jsonl", "output-generation", "session-id" })
            Assert.True(File.Exists(Path.Combine(state, file)), $"{file} is not in {state}");
        Assert.Equal(created.Id, harness.ReadStatusFile(created.Id).Id);
        // Its MCP config, with its token, is out of the working folder
        Assert.Equal(harness.McpConfigPath(created.Id), (await harness.WaitForStdinAsync(created.Id)).ArgValue("--mcp-config"));
        Assert.False(File.Exists(Path.Combine(state, GodMode.Server.Services.McpConfigFile.FileName)));
    }

    /// <summary>Without a kind from its script, a session's kind is its action's name, in the id and in the status.</summary>
    [Fact]
    public async Task Kind_IsTheActionsName_WhenTheScriptNamesNone()
    {
        await using var harness = new LifecycleHarness(Waiting());

        var created = await harness.CreateProjectAsync("p1");

        Assert.Equal("create", created.Kind);
        Assert.Matches(LifecycleHarness.IdPattern("p1", kind: "create"), created.Id);
        Assert.Equal("create", Assert.Single(await harness.Projects.ListProjectsAsync()).Kind);
        Assert.Equal("create", harness.ReadStatusFile(created.Id).Kind);
    }

    /// <summary>
    /// A create script's <c>kind</c> is the session's, in the id and in the status and its summary, and
    /// its <c>project_name</c> the id's slug; the script was told the id as the action makes it.
    /// </summary>
    [Fact]
    public async Task Kind_IsTheCreateScripts_WhenItsResultNamesOne_AndTheNameItReturnsIsTheSlug()
    {
        await using var harness = new LifecycleHarness(Waiting(), rootConfig: new Dictionary<string, object> { ["create"] = "create.ps1" });
        var seen = Path.Combine(harness.WorkDir, "seen.txt");
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "create.ps1"), $"""
            Set-Content -Path '{seen}' -Value $env:GODMODE_SESSION_ID
            Set-Content -Path $env:GODMODE_RESULT_FILE -Value "kind=Bug`nproject_name=Crash on start"
            """);

        var created = await harness.CreateProjectAsync("42");

        Assert.Equal("bug", created.Kind);
        Assert.Equal("Crash on start", created.Name);
        Assert.Matches(LifecycleHarness.IdPattern("crash-on-start", kind: "bug"), created.Id);
        Assert.Equal("bug", Assert.Single(await harness.Projects.ListProjectsAsync()).Kind);
        Assert.Equal("bug", harness.ReadStatusFile(created.Id).Kind);
        // Told before its result: the action's kind and the name it was given, with the suffix the id keeps
        var told = File.ReadAllText(seen).Trim();
        Assert.Matches($"^{DateTime.Now:yyMMdd}-create-42-[a-z2-7]{{4}}$", told);
        Assert.EndsWith(told[^4..], created.Id);
    }

    /// <summary>Scripts that run once the session exists (here, delete) are told its id.</summary>
    [Fact]
    public async Task DeleteScript_IsToldTheSessionsId()
    {
        await using var harness = new LifecycleHarness(Waiting(), rootConfig: new Dictionary<string, object> { ["delete"] = "delete.ps1" });
        var seen = Path.Combine(harness.WorkDir, "seen.txt");
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "delete.ps1"), $"Set-Content -Path '{seen}' -Value $env:GODMODE_SESSION_ID");
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(created.Id);

        await harness.Projects.DeleteProjectAsync(created.Id);

        Assert.Equal(created.Id.Split('/')[^1], File.ReadAllText(seen).Trim());
    }

    /// <summary>
    /// Two sessions of one name on one day are two ids: the date, kind and slug are the same, the
    /// suffix is not. Here a create script puts each in a folder of its own and names both the same.
    /// </summary>
    [Fact]
    public async Task TwoSessionsOfOneName_OnOneDay_HaveTwoIds()
    {
        await using var harness = new LifecycleHarness(Waiting(),
            rootConfig: new Dictionary<string, object> { ["scriptsCreateFolder"] = true, ["create"] = "create.ps1" });
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "create.ps1"), """
            $folder = Join-Path $env:GODMODE_ROOT_PATH $env:GODMODE_INPUT_FOLDER
            New-Item -ItemType Directory -Force $folder | Out-Null
            Set-Content -Path $env:GODMODE_RESULT_FILE -Value "project_path=$folder`nproject_name=same"
            """);

        var first = await harness.CreateProjectAsync("a", inputs: new Dictionary<string, object> { ["folder"] = "one" });
        var second = await harness.CreateProjectAsync("b", inputs: new Dictionary<string, object> { ["folder"] = "two" });

        Assert.Matches(LifecycleHarness.IdPattern("same"), first.Id);
        Assert.Matches(LifecycleHarness.IdPattern("same"), second.Id);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, (await harness.Projects.ListProjectsAsync()).Length);
    }

    /// <summary>A restart finds the session in its state folder: the same ID, kind, name and claude session.</summary>
    [Fact]
    public async Task Restart_RecoversTheSession_FromItsStateFolder()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var created = await harness.CreateProjectAsync("Left list");
        var launch = await harness.WaitForStdinAsync(created.Id);

        await harness.RestartAsync(resume: false);

        var recovered = Assert.Single(await harness.Projects.ListProjectsAsync());
        Assert.Equal(created.Id, recovered.Id);
        Assert.Equal("create", recovered.Kind);
        Assert.Equal("Left list", recovered.Name);
        Assert.Equal(harness.ProjectPath(created.Id), harness.Tracked(created.Id).ProjectPath);
        Assert.Equal(launch.ArgValue("--session-id"), harness.Tracked(created.Id).ClaudeSessionId);
    }

    /// <summary>A delete removes the session, its state with its working folder.</summary>
    [Fact]
    public async Task Delete_RemovesTheSession()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var created = await harness.CreateProjectAsync();
        await harness.WaitForStdinAsync(created.Id);
        var state = harness.StatePath(created.Id);

        await harness.Projects.DeleteProjectAsync(created.Id);

        Assert.False(Directory.Exists(state), $"{state} is still there");
        Assert.Empty(await harness.Projects.ListProjectsAsync());
        await harness.RestartAsync(resume: false);
        Assert.Empty(await harness.Projects.ListProjectsAsync());
    }

    /// <summary>
    /// The old flat layout (<c>.godmode/status.json</c> and the rest) is not read: a folder with only
    /// that is no session, and nothing of it is changed. A folder in <c>sessions/</c> whose name is no
    /// id, or that has no status.json, is none either.
    /// </summary>
    [Fact]
    public async Task OldFlatGodMode_AndFoldersThatAreNoSession_AreIgnored()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var now = DateTime.UtcNow;
        var old = new ProjectStatus("old", "old", ProjectState.Stopped, now, now, null, new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0);
        var flat = Path.Combine(harness.RootPath, "old", ".godmode");
        Directory.CreateDirectory(flat);
        File.WriteAllText(Path.Combine(flat, "status.json"), JsonSerializer.Serialize(old, JsonDefaults.Options));
        File.WriteAllText(Path.Combine(flat, "session-id"), Guid.NewGuid().ToString());
        LifecycleHarness.PlantSession(Path.Combine(harness.RootPath, "odd"), "not-an-id", old);
        LifecycleHarness.PlantSession(Path.Combine(harness.RootPath, "empty"));
        var before = File.ReadAllText(Path.Combine(flat, "status.json"));

        await harness.RestartAsync(resume: false);

        Assert.Empty(await harness.Projects.ListProjectsAsync());
        Assert.Equal(before, File.ReadAllText(Path.Combine(flat, "status.json")));
    }

    /// <summary>
    /// A worktree is a working folder with one session: its state, the MCP config with the session's
    /// token among it, is all out of git, whatever the checkout's own .gitignore says.
    /// </summary>
    [Fact]
    public async Task SessionInAGitWorkingFolder_LeavesGitStatusClean()
    {
        await using var harness = new LifecycleHarness(Waiting(),
            rootConfig: new Dictionary<string, object> { ["scriptsCreateFolder"] = true, ["create"] = "checkout.ps1" });
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "checkout.ps1"), """
            $ErrorActionPreference = 'Stop'
            New-Item -ItemType Directory -Force $env:GODMODE_PROJECT_PATH | Out-Null
            git -C $env:GODMODE_PROJECT_PATH init -q
            Set-Content -Path (Join-Path $env:GODMODE_PROJECT_PATH 'README.md') -Value 'checked out'
            git -C $env:GODMODE_PROJECT_PATH add README.md
            git -C $env:GODMODE_PROJECT_PATH -c user.name=t -c user.email=t@t commit -q -m checkout
            """);

        var created = await harness.CreateProjectAsync("wt");
        await harness.WaitForStdinAsync(created.Id);

        Assert.True(File.Exists(McpConfigPath(harness, created.Id)), "the MCP config is not there while claude runs");
        var status = Git(harness.ProjectPath(created.Id), "status --porcelain --untracked-files=all");
        // The fake records its launches in the folder it runs in; that is the fake's, not GodMode's
        Assert.Equal([], status.Where(line => !(line.Contains("fake-claude-", StringComparison.Ordinal) && line.EndsWith(".jsonl", StringComparison.Ordinal))));
    }

    private static string McpConfigPath(LifecycleHarness harness, string projectId) =>
        harness.McpConfigPath(projectId);

    private static string[] Git(string folder, string arguments)
    {
        using var git = Process.Start(new ProcessStartInfo("git", $"-C \"{folder}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        Assert.Equal(0, git.ExitCode);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
