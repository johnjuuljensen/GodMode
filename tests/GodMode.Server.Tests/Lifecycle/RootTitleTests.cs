using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A root's title (issue #434): what clients show for it, while its name stays its key. It is its config.json's
/// <c>title</c>, else its explicit entry's <c>Title</c> (as <c>profileName</c> wins over <c>Profile</c>), else none.
/// </summary>
public sealed class RootTitleTests : IDisposable
{
    private readonly string _elsewhere = ServerProcess.CreateWorkDir("root-title");

    public void Dispose() => ServerProcess.DeleteWorkDir(_elsewhere);

    private static FakeScript Waiting() => new FakeScript().EmitInit().AwaitStdin();

    private string WriteRoot(string name, string? title)
    {
        var path = Path.Combine(_elsewhere, name);
        var configDir = Path.Combine(path, ".godmode-root");
        Directory.CreateDirectory(configDir);
        var config = new Dictionary<string, string> { ["profileName"] = "Mega" };
        if (title != null) config["title"] = title;
        File.WriteAllText(Path.Combine(configDir, "config.json"), JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<ProjectRootInfo> RootAsync(LifecycleHarness harness, string name) =>
        Assert.Single(await harness.Projects.ListProjectRootsAsync(), root => root.Name == name);

    [Fact]
    public async Task ConfigJsonsTitle_IsListedWithTheRoot_WhoseNameStaysItsKey()
    {
        await using var harness = new LifecycleHarness(Waiting(), rootConfig: new Dictionary<string, object> { ["title"] = "Assistant" });

        var root = await RootAsync(harness, LifecycleHarness.RootName);

        Assert.Equal("Assistant", root.Title);
        Assert.Equal(LifecycleHarness.ProfileName, root.ProfileName);
    }

    [Fact]
    public async Task ARootWithNoTitle_OrABlankOne_HasNone()
    {
        await using var harness = new LifecycleHarness(Waiting(), rootConfig: new Dictionary<string, object> { ["title"] = "  " });

        Assert.Null((await RootAsync(harness, LifecycleHarness.RootName)).Title);
    }

    [Fact]
    public async Task AnExplicitEntrysTitle_IsTheRootsWhenItsConfigJsonHasNone_AndConfigJsonsWins()
    {
        await using var harness = new LifecycleHarness(Waiting(), settings: new Dictionary<string, string?>
        {
            ["Roots:Explicit:Mega-Assistant:Path"] = WriteRoot("mega", title: null),
            ["Roots:Explicit:Mega-Assistant:Title"] = "Assistant",
            ["Roots:Explicit:Mega-Notes:Path"] = WriteRoot("notes", title: "Notes"),
            ["Roots:Explicit:Mega-Notes:Title"] = "Entry's notes",
            ["Roots:Explicit:Mega-Plain:Path"] = WriteRoot("plain", title: null),
        });

        Assert.Equal("Assistant", (await RootAsync(harness, "Mega-Assistant")).Title);
        Assert.Equal("Notes", (await RootAsync(harness, "Mega-Notes")).Title);
        Assert.Null((await RootAsync(harness, "Mega-Plain")).Title);
    }

    /// <summary>The title is display only: a create is keyed by the root's name, and its ID has the name.</summary>
    [Fact]
    public async Task ACreateInATitledRoot_IsKeyedByItsName()
    {
        await using var harness = new LifecycleHarness(Waiting(), rootConfig: new Dictionary<string, object> { ["title"] = "Assistant" });

        var created = await harness.CreateProjectAsync("titled");

        Assert.StartsWith($"{LifecycleHarness.ProfileName}/{LifecycleHarness.RootName}/", created.Id);
        Assert.Equal(LifecycleHarness.RootName, created.RootName);
    }
}
