using System.Text.Json;
using System.Text.Json.Nodes;
using GodMode.FakeClaude;
using GodMode.Server.Auth;
using GodMode.Server.Services;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests.Lifecycle;

/// <summary>
/// A server's roots come from its config: several scan folders (<c>Roots:Scan:&lt;key&gt;</c>), roots
/// named explicitly anywhere on disk (<c>Roots:Explicit:&lt;name&gt;</c>), and each profile's
/// description and environment (<c>Profiles:&lt;name&gt;</c>). One name, one root per server: an
/// explicit root wins a clash, and between scan folders the first key in ordinal order does.
/// <c>.profiles/</c> is not read.
/// </summary>
public sealed class RootSourcesTests : IDisposable
{
    /// <summary>Folders outside the harness's own scan folder: more scan folders, and explicit roots.</summary>
    private readonly string _elsewhere = ServerProcess.CreateWorkDir("root-sources");

    public void Dispose() => ServerProcess.DeleteWorkDir(_elsewhere);

    private static FakeScript Waiting() => new FakeScript().EmitInit().AwaitStdin();

    /// <summary>A root folder at <paramref name="path"/>: a <c>.godmode-root/config.json</c> naming <paramref name="profile"/>, or no profile.</summary>
    private static string WriteRoot(string path, string? profile = null, string? description = null)
    {
        var configDir = Path.Combine(path, ".godmode-root");
        Directory.CreateDirectory(configDir);
        var config = new Dictionary<string, string>();
        if (profile != null) config["profileName"] = profile;
        if (description != null) config["description"] = description;
        File.WriteAllText(Path.Combine(configDir, "config.json"), JsonSerializer.Serialize(config));
        return path;
    }

    private string Elsewhere(params string[] parts) => Path.Combine([_elsewhere, .. parts]);

    private static async Task<ProjectRootInfo> SingleRootAsync(LifecycleHarness harness, string name) =>
        Assert.Single(await harness.Projects.ListProjectRootsAsync(), root => root.Name == name);

    private static string[] ClashLines(LifecycleHarness harness) =>
        harness.Warnings.Where(line => line.Contains("clashes with the root")).ToArray();

    [Fact]
    public async Task TwoScanFolders_ListTheRootsOfBoth()
    {
        WriteRoot(Elsewhere("more-roots", "second"), "second-profile");
        await using var harness = new LifecycleHarness(Waiting(), settings: new Dictionary<string, string?>
        {
            ["Roots:Scan:more"] = Elsewhere("more-roots"),
        });

        var roots = await harness.Projects.ListProjectRootsAsync();

        Assert.Equal(LifecycleHarness.ProfileName, Assert.Single(roots, root => root.Name == LifecycleHarness.RootName).ProfileName);
        Assert.Equal("second-profile", Assert.Single(roots, root => root.Name == "second").ProfileName);
        Assert.Contains(await harness.Projects.ListProfilesAsync(), profile => profile.Name == "second-profile");
    }

    /// <summary>
    /// Config sources merge the scan folders entry by entry: a later source (the instance's file over
    /// appsettings) replaces the one entry it names and keeps the rest. As an array, .NET would merge
    /// them by index, and the later source's one entry would replace the first of the earlier ones.
    /// </summary>
    [Fact]
    public async Task ScanFolders_MergeAcrossConfigSources_EntryByEntry()
    {
        WriteRoot(Elsewhere("a", "root-a"), "p");
        WriteRoot(Elsewhere("b", "root-b"), "p");
        WriteRoot(Elsewhere("c", "root-c"), "p");
        var earlier = Elsewhere("appsettings.json");
        var later = Elsewhere("instance.json");
        File.WriteAllText(earlier, JsonSerializer.Serialize(new { Roots = new { Scan = new { a = Elsewhere("a"), b = Elsewhere("b") } } }));
        File.WriteAllText(later, JsonSerializer.Serialize(new { Roots = new { Scan = new { b = Elsewhere("c") } } }));
        await using var harness = new LifecycleHarness(Waiting(), configFiles: [earlier, later]);

        var names = (await harness.Projects.ListProjectRootsAsync()).Select(root => root.Name).ToArray();

        Assert.Contains("root-a", names);
        Assert.Contains("root-c", names);
        Assert.DoesNotContain("root-b", names);
        Assert.Contains(LifecycleHarness.RootName, names);
    }

    /// <summary>
    /// An explicit root's profile is its entry's <c>Profile</c>, else its config.json's <c>profileName</c>, else
    /// <c>Default</c> (#344): the host's config, which holds the profiles' secrets, picks whose a root gets.
    /// </summary>
    [Fact]
    public async Task ExplicitRoot_OutsideEveryScanFolder_TakesItsProfileFromTheEntry_ElseFromItsConfig_ElseDefault()
    {
        WriteRoot(Elsewhere("anywhere", "own"), profile: "from-config");
        WriteRoot(Elsewhere("anywhere", "entry"));
        WriteRoot(Elsewhere("anywhere", "config-only"), profile: "from-config");
        WriteRoot(Elsewhere("anywhere", "bare"));
        await using var harness = new LifecycleHarness(Waiting(), settings: new Dictionary<string, string?>
        {
            // The entry wins over the root's own config.json
            ["Roots:Explicit:own:Path"] = Elsewhere("anywhere", "own"),
            ["Roots:Explicit:own:Profile"] = "from-entry",
            ["Roots:Explicit:solo:Path"] = Elsewhere("anywhere", "entry"),
            ["Roots:Explicit:solo:Profile"] = "from-entry",
            ["Roots:Explicit:configured:Path"] = Elsewhere("anywhere", "config-only"),
            ["Roots:Explicit:bare:Path"] = Elsewhere("anywhere", "bare"),
        });

        Assert.Equal("from-entry", (await SingleRootAsync(harness, "own")).ProfileName);
        Assert.Equal("from-entry", (await SingleRootAsync(harness, "solo")).ProfileName);
        Assert.Equal("from-config", (await SingleRootAsync(harness, "configured")).ProfileName);
        Assert.Equal("Default", (await SingleRootAsync(harness, "bare")).ProfileName);
        Assert.Contains(await harness.Projects.ListProjectRootsAsync(), root => root.Name == LifecycleHarness.RootName);
    }

    [Fact]
    public async Task ExplicitRoot_CreatesItsProjectsInItsOwnFolder()
    {
        var solo = WriteRoot(Elsewhere("anywhere", "solo-folder"), profile: "solo-profile");
        await using var harness = new LifecycleHarness(Waiting(), settings: new Dictionary<string, string?>
        {
            ["Roots:Explicit:solo:Path"] = solo,
        });

        var created = (await harness.Projects.CreateProjectAsync(new CreateProjectRequest("solo-profile", "solo",
            new Dictionary<string, JsonElement> { ["name"] = JsonSerializer.SerializeToElement("in-solo") }))).Project!;

        Assert.Matches(LifecycleHarness.IdPattern("in-solo", root: "solo", profile: "solo-profile"), created.Id);
        Assert.True(Directory.Exists(GodMode.ProjectFiles.SessionState.PathOf(Path.Combine(solo, "in-solo"), created.Id.Split('/')[^1])), "the session's state is not in its folder in the explicit root's folder");
    }

    [Fact]
    public async Task ExplicitRoot_WinsANameClash_OverAScannedRoot_AndTheLoserIsLoggedOnceWithBothPaths()
    {
        var explicitPath = WriteRoot(Elsewhere("anywhere", "the-explicit-one"), profile: "explicit-profile");
        await using var harness = new LifecycleHarness(Waiting(), settings: new Dictionary<string, string?>
        {
            [$"Roots:Explicit:{LifecycleHarness.RootName}:Path"] = explicitPath,
        });

        for (var i = 0; i < 3; i++)
            Assert.Equal("explicit-profile", (await SingleRootAsync(harness, LifecycleHarness.RootName)).ProfileName);

        var clash = Assert.Single(ClashLines(harness));
        Assert.Contains($"at {harness.RootPath} ({LifecycleHarness.ScanSetting})", clash);
        Assert.Contains($"at {explicitPath} (Roots:Explicit:{LifecycleHarness.RootName})", clash);
    }

    [Fact]
    public async Task BetweenScanFolders_TheFirstKeyInOrdinalOrderWins_AndEachLoserIsLogged()
    {
        // "aa" < "test" (the harness's) < "zz"
        var early = WriteRoot(Elsewhere("early", LifecycleHarness.RootName), "early-profile");
        var late = WriteRoot(Elsewhere("late", LifecycleHarness.RootName), "late-profile");
        await using var harness = new LifecycleHarness(Waiting(), settings: new Dictionary<string, string?>
        {
            ["Roots:Scan:zz"] = Elsewhere("late"),
            ["Roots:Scan:aa"] = Elsewhere("early"),
        });

        Assert.Equal("early-profile", (await SingleRootAsync(harness, LifecycleHarness.RootName)).ProfileName);
        await harness.Projects.ListProfilesAsync();

        var clashes = ClashLines(harness);
        Assert.Equal(2, clashes.Length);
        Assert.All(clashes, line => Assert.Contains($"at {early} (Roots:Scan:aa)", line));
        Assert.Contains(clashes, line => line.Contains($"at {harness.RootPath} ({LifecycleHarness.ScanSetting}) is skipped"));
        Assert.Contains(clashes, line => line.Contains($"at {late} (Roots:Scan:zz) is skipped"));
    }

    /// <summary>One folder, one root: an explicit root under another name is that folder's root, and the scan's is skipped.</summary>
    [Fact]
    public async Task ExplicitRootOnAScannedFolder_UnderAnotherName_WinsTheFolder()
    {
        var scanned = WriteRoot(Elsewhere("more", "scanned-name"), "p");
        await using var harness = new LifecycleHarness(Waiting(), settings: new Dictionary<string, string?>
        {
            ["Roots:Scan:more"] = Elsewhere("more"),
            ["Roots:Explicit:alias:Path"] = scanned,
        });

        var names = (await harness.Projects.ListProjectRootsAsync()).Select(root => root.Name).ToArray();

        Assert.Contains("alias", names);
        Assert.DoesNotContain("scanned-name", names);
        var clash = Assert.Single(ClashLines(harness));
        Assert.Contains($"Root scanned-name at {scanned} (Roots:Scan:more) is skipped: it clashes with the root alias at {scanned}", clash);
    }

    /// <summary>An explicit root that is also in a scan folder, under the same name, is the one root, and no clash.</summary>
    [Fact]
    public async Task ExplicitRootInAScanFolder_UnderItsOwnName_IsOneRoot()
    {
        var inside = WriteRoot(Elsewhere("more", "inside"));
        await using var harness = new LifecycleHarness(Waiting(), settings: new Dictionary<string, string?>
        {
            ["Roots:Scan:more"] = Elsewhere("more"),
            ["Roots:Explicit:inside:Path"] = inside,
            ["Roots:Explicit:inside:Profile"] = "explicit-profile",
        });

        Assert.Equal("explicit-profile", (await SingleRootAsync(harness, "inside")).ProfileName);
        Assert.Empty(ClashLines(harness));
    }

    [Fact]
    public async Task ExplicitRootThatDoesNotExist_IsNotListed_AndIsLoggedOnce()
    {
        var missing = Elsewhere("not-there");
        await using var harness = new LifecycleHarness(Waiting(), settings: new Dictionary<string, string?>
        {
            ["Roots:Explicit:ghost:Path"] = missing,
        });

        await harness.Projects.ListProjectRootsAsync();
        Assert.DoesNotContain(await harness.Projects.ListProjectRootsAsync(), root => root.Name == "ghost");

        var line = Assert.Single(harness.Warnings, line => line.Contains("ghost"));
        Assert.Contains($"{missing} (Roots:Explicit:ghost:Path) does not exist", line);
    }

    [Fact]
    public async Task ProfileDescription_ComesFromConfig()
    {
        await using var harness = new LifecycleHarness(Waiting(), settings: new Dictionary<string, string?>
        {
            [$"Profiles:{LifecycleHarness.ProfileName}:Description"] = "The work profile",
        });

        var profile = Assert.Single(await harness.Projects.ListProfilesAsync(), profile => profile.Name == LifecycleHarness.ProfileName);
        Assert.Equal("The work profile", profile.Description);
    }

    /// <summary>The profile's environment reaches its sessions, under the root's own: the root's <c>environment</c> wins a clash.</summary>
    [Fact]
    public async Task ProfileEnvironment_ReachesTheSession_AndTheRootsEnvironmentWinsAClash()
    {
        await using var harness = new LifecycleHarness(Waiting(), profileEnvironment: new Dictionary<string, string>
        {
            ["FROM_PROFILE"] = "profile-value",
            ["CLASHING"] = "profile-value",
        });
        var configPath = Path.Combine(harness.RootPath, ".godmode-root", "config.json");
        var config = JsonNode.Parse(File.ReadAllText(configPath))!;
        config["environment"]!["CLASHING"] = "root-value";
        File.WriteAllText(configPath, config.ToJsonString());

        var created = await harness.CreateProjectAsync();
        var launch = await harness.WaitForStdinAsync(created.Id);

        Assert.Equal("profile-value", launch.Environment["FROM_PROFILE"]);
        Assert.Equal("root-value", launch.Environment["CLASHING"]);
    }

    /// <summary>A profile's environment reaches only its own profile's sessions.</summary>
    [Fact]
    public async Task ProfileEnvironment_OfAnotherProfile_DoesNotReachTheSession()
    {
        await using var harness = new LifecycleHarness(Waiting(), settings: new Dictionary<string, string?>
        {
            ["Profiles:someone-else:Environment:FROM_OTHER"] = "other-value",
        });

        var created = await harness.CreateProjectAsync();
        var launch = await harness.WaitForStdinAsync(created.Id);

        Assert.False(launch.Environment.ContainsKey("FROM_OTHER"));
    }

    /// <summary>
    /// <c>.profiles/</c> is gone: neither its description nor its environment is read, and it is no root. One
    /// left in a scan folder is warned about, once, since it may still hold the secrets it once gave (#344).
    /// </summary>
    [Fact]
    public async Task DotProfilesFolder_IsNotRead()
    {
        await using var harness = new LifecycleHarness(Waiting());
        var profileDir = Path.Combine(harness.RootsDir, ".profiles", LifecycleHarness.ProfileName);
        Directory.CreateDirectory(profileDir);
        File.WriteAllText(Path.Combine(profileDir, "profile.json"), """{ "description": "from .profiles" }""");
        File.WriteAllText(Path.Combine(profileDir, "env.json"), """{ "FROM_DOT_PROFILES": "x" }""");

        var profiles = await harness.Projects.ListProfilesAsync();
        var created = await harness.CreateProjectAsync();
        var launch = await harness.WaitForStdinAsync(created.Id);

        Assert.NotEqual("from .profiles", Assert.Single(profiles, profile => profile.Name == LifecycleHarness.ProfileName).Description);
        Assert.False(launch.Environment.ContainsKey("FROM_DOT_PROFILES"));
        Assert.DoesNotContain(await harness.Projects.ListProjectRootsAsync(), root => root.Name == ".profiles");
        var leftover = Assert.Single(harness.Warnings, line => line.Contains(Path.Combine(harness.RootsDir, ".profiles")));
        Assert.Contains("is not read", leftover);
    }

    /// <summary>The settings that once named roots are not read, and each is named at startup with what takes its place.</summary>
    [Fact]
    public async Task RetiredRootSettings_AreNotRead_AndAreNamedAtStartup()
    {
        WriteRoot(Elsewhere("old-roots", "old-scanned"), "p");
        WriteRoot(Elsewhere("anywhere", "old-explicit"), "p");
        await using var harness = new LifecycleHarness(Waiting(), settings: new Dictionary<string, string?>
        {
            ["ProjectRootsDir"] = Elsewhere("old-roots"),
            ["Profiles:Mega:Roots:old-explicit"] = Elsewhere("anywhere", "old-explicit"),
        });

        var names = (await harness.Projects.ListProjectRootsAsync()).Select(root => root.Name).ToArray();

        Assert.DoesNotContain("old-scanned", names);
        Assert.DoesNotContain("old-explicit", names);
        Assert.Contains(harness.Warnings, line => line.Contains($"ProjectRootsDir ({Elsewhere("old-roots")}) is not read: name the folder as a scan folder, Roots:Scan:<key>"));
        Assert.Contains(harness.Warnings, line => line.Contains(
            "Profiles:Mega:Roots:old-explicit (" + Elsewhere("anywhere", "old-explicit") + ") is not read: name it as an explicit root, Roots:Explicit:old-explicit:Path, with :Profile Mega"));
    }

    /// <summary>
    /// The key file is never where sessions work. The start refuses a root source that holds it; one
    /// added to the config while the server runs (the instance file reloads) is left out, logged once,
    /// and the roots beside it stay listed.
    /// </summary>
    [Fact]
    public async Task RootSourceAddedLater_WhoseTreeHoldsTheKeyFile_IsLeftOut()
    {
        var keyFile = Elsewhere("home", "AppData", "GodMode.Server", "api-key");
        Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
        File.WriteAllText(keyFile, "the-key");
        WriteRoot(Elsewhere("home", "AppData", "scanned-with-the-key"), "p");
        var instanceFile = Elsewhere("instance.json");
        File.WriteAllText(instanceFile, "{}");
        await using var harness = new LifecycleHarness(Waiting(),
            configFiles: [instanceFile],
            auth: new AuthSettings(AuthMode.ApiKey, "the-key", KeyFilePath: keyFile));
        Assert.Contains(await harness.Projects.ListProjectRootsAsync(), root => root.Name == LifecycleHarness.RootName);

        // Added while it runs: an explicit root over the key file's tree, a scan folder over it, and one beside it
        WriteRoot(Elsewhere("elsewhere", "fine"), "p");
        File.WriteAllText(instanceFile, JsonSerializer.Serialize(new
        {
            Roots = new
            {
                Scan = new { home = Elsewhere("home", "AppData"), beside = Elsewhere("elsewhere") },
                Explicit = new { homed = new { Path = Elsewhere("home") } },
            },
        }));
        harness.ReloadConfiguration();

        for (var i = 0; i < 2; i++)
        {
            var names = (await harness.Projects.ListProjectRootsAsync()).Select(root => root.Name).ToArray();
            Assert.DoesNotContain("homed", names);
            Assert.DoesNotContain("scanned-with-the-key", names);
            Assert.Contains("fine", names);
            Assert.Contains(LifecycleHarness.RootName, names);
        }

        var leftOut = harness.Warnings.Where(line => line.Contains("is left out: the server's API key file")).ToArray();
        Assert.Equal(2, leftOut.Length);
        Assert.Contains(leftOut, line => line.Contains($"Roots:Explicit:homed:Path ({Elsewhere("home")})"));
        Assert.Contains(leftOut, line => line.Contains($"Roots:Scan:home ({Elsewhere("home", "AppData")})"));
        Assert.All(leftOut, line => Assert.Contains(keyFile, line));
    }

    /// <summary>One server per root holds for explicit roots too: a second server naming the same folder leaves it alone.</summary>
    [Fact]
    public async Task ExplicitRoot_HeldByAnotherServer_IsLeftAlone()
    {
        var solo = WriteRoot(Elsewhere("anywhere", "solo"), profile: "solo-profile");
        Dictionary<string, string?> Settings(string instance) => new()
        {
            [ProjectManager.InstanceSetting] = instance,
            ["Roots:Explicit:solo:Path"] = solo,
        };
        await using var main = new LifecycleHarness(Waiting(), settings: Settings("main"));
        await using var dev = new LifecycleHarness(Waiting(), settings: Settings("dev"));

        Assert.Contains(await main.Projects.ListProjectRootsAsync(), root => root.Name == "solo");
        Assert.DoesNotContain(await dev.Projects.ListProjectRootsAsync(), root => root.Name == "solo");
        Assert.Contains(dev.Warnings, line => line.Contains($"/solo at {solo} is held by another server (instance main"));

        main.StopHost();

        Assert.Contains(await dev.Projects.ListProjectRootsAsync(), root => root.Name == "solo");
    }
}
