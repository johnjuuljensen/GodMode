using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Shared;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// GodMode provisions nothing: a session gets one MCP server, GodMode's own, and none of its tools
/// is pre-approved. A repo brings its MCP servers in its own <c>.mcp.json</c>, and user-scoped ones
/// live in the profile's <c>CLAUDE_CONFIG_DIR</c>. MCP config a root still carries is
/// logged once as ignored, and a root's leftover <c>.archived</c> folder is not a project.
/// </summary>
public class NoProvisioningTests
{
    /// <summary>MCP servers in the root's config.json, where GodMode once read them.</summary>
    private static readonly Dictionary<string, object> RootMcpServers = new()
    {
        ["mcpServers"] = new Dictionary<string, object> { ["from-root"] = new { command = "root-cmd" } },
    };

    [Fact]
    public async Task McpConfig_HoldsOnlyGodModesOwnServer_WhateverTheRootStillCarries()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin(), rootConfig: RootMcpServers);
        WriteActionMcpServers(harness);

        var created = await harness.CreateProjectAsync();
        var launch = await harness.WaitForStdinAsync(created.Id);

        var godMode = GodModeMcpEntry.Parse(McpConfig(launch));
        Assert.Equal("http", godMode.Type);
    }

    /// <summary>
    /// No <c>--allowedTools</c>: every MCP tool that needs approval reaches the permission prompt,
    /// unless Claude Code's own settings allow it. Not even when the profile's Claude config dir, or
    /// what the root still carries, lists MCP servers.
    /// </summary>
    [Fact]
    public async Task NothingIsPreApproved_EvenWhenTheProfilesClaudeConfigListsMcpServers()
    {
        var configDir = ServerProcess.CreateWorkDir("claudecfg");
        try
        {
            File.WriteAllText(Path.Combine(configDir, "settings.json"), """{ "mcpServers": { "from-settings": { "command": "settings-cmd" } } }""");
            File.WriteAllText(Path.Combine(configDir, ".claude.json"), """{ "mcpServers": { "from-user-scope": { "command": "user-cmd" } } }""");
            await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin(), rootConfig: RootMcpServers,
                profileEnvironment: new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = configDir });
            WriteActionMcpServers(harness);

            var created = await harness.CreateProjectAsync();
            var launch = await harness.WaitForStdinAsync(created.Id);

            Assert.Equal(configDir, launch.Environment["CLAUDE_CONFIG_DIR"]);
            Assert.DoesNotContain("--dangerously-skip-permissions", launch.Argv);
            Assert.DoesNotContain(launch.Argv, arg => arg is "--allowedTools" or "--allowed-tools");
        }
        finally
        {
            ServerProcess.DeleteWorkDir(configDir);
        }
    }

    /// <summary>Every listing, launch and resume reads the root's config again; the warning is said once.</summary>
    [Fact]
    public async Task RootConfigWithMcpServers_LaunchesNormally_AndIsLoggedOnceAsIgnored()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().Turn("Done.").AwaitStdin(), rootConfig: RootMcpServers);

        var created = await harness.CreateProjectAsync();
        await harness.WaitForStateAsync(created.Id, ProjectState.Idle);
        await harness.Projects.ListProjectRootsAsync();
        await harness.Projects.ListProfilesAsync();
        await harness.Projects.StopProjectAsync(created.Id);
        await harness.WaitForStateAsync(created.Id, ProjectState.Stopped);
        await harness.Projects.ResumeProjectAsync(created.Id);
        await harness.WaitForLaunchAsync(created.Id, _ => true, index: 1);

        var warning = Assert.Single(harness.Warnings, line => line.Contains("mcpServers"));
        Assert.Contains(" Warning RootConfigReader: ", warning);
        Assert.Contains(Path.Combine(harness.RootPath, ".godmode-root", "config.json"), warning);
        Assert.Contains("which GodMode ignores", warning);
        // The resumed fake starts its session at once, which a bare resume waits for
        await harness.WaitForStateAsync(created.Id, ProjectState.Running);
    }

    /// <summary>
    /// The server archives nothing, so a root's <c>.archived</c> folder is just a folder: a start
    /// recovers nothing under it, says nothing about it, and leaves it as it was.
    /// </summary>
    [Fact]
    public async Task LeftoverArchivedFolder_IsNotAProject_AndIsLeftAlone()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin());
        var archiveDir = Path.Combine(harness.RootPath, ".archived");
        WriteArchivedProject(Path.Combine(archiveDir, "old-one"));
        var before = Tree(archiveDir);

        await harness.RestartAsync();

        Assert.Empty(await harness.Projects.ListProjectsAsync());
        Assert.Empty(harness.Warnings);
        Assert.Equal(before, Tree(archiveDir));
    }

    /// <summary>MCP servers where GodMode once read them beside the root's: an overlay for the root's one action.</summary>
    private static void WriteActionMcpServers(LifecycleHarness harness) =>
        File.WriteAllText(Path.Combine(harness.RootPath, ".godmode-root", "config.Create.json"),
            """{ "mcpServers": { "from-action": { "url": "https://mcp.example.test/mcp" } } }""");

    /// <summary>A project folder as an older server archived it: its session's state, and archive.json beside it.</summary>
    private static void WriteArchivedProject(string folder)
    {
        var godMode = LifecycleHarness.PlantSession(folder);
        var now = DateTime.UtcNow;
        var status = new ProjectStatus(LifecycleHarness.PlantedId(), "Old one", ProjectState.Stopped, now, now, null,
            new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0);
        File.WriteAllText(Path.Combine(godMode, "status.json"), JsonSerializer.Serialize(status, JsonDefaults.Options));
        File.WriteAllText(Path.Combine(godMode, "archive.json"), """{ "ArchivedAt": "2026-01-01T00:00:00Z", "Name": "Old one" }""");
    }

    /// <summary>The MCP config the launch was given; read while it runs, since its exit deletes it.</summary>
    private static string McpConfig(FakeLaunch launch) =>
        File.ReadAllText(launch.ArgValue("--mcp-config") ?? throw new InvalidOperationException("no --mcp-config"));

    /// <summary>Every directory and file under <paramref name="dir"/>, with each file's size and last write.</summary>
    private static string[] Tree(string dir) =>
        Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
            .Select(path => File.Exists(path)
                ? $"{Path.GetRelativePath(dir, path)} {new FileInfo(path).Length} {File.GetLastWriteTimeUtc(path):O}"
                : Path.GetRelativePath(dir, path) + "/")
            .Order(StringComparer.Ordinal)
            .ToArray();
}
