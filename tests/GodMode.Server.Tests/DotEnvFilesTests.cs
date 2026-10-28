using System.Text.Json.Nodes;
using GodMode.FakeClaude;
using GodMode.Server.Services;
using GodMode.Server.Tests.Lifecycle;
using GodMode.Shared.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodMode.Server.Tests;

/// <summary>
/// Issue #614: a root's environment can come from the <c>.env</c> files from its folder up to the drive's root,
/// nearer winning key by key. The layer lies over the profile's environment and under the root's
/// <c>config.json</c> <c>environment</c> and the action's overlay, and is read fresh on each operation.
/// </summary>
public class DotEnvFilesTests
{
    [Theory]
    [InlineData("A=1", "A", "1")]
    [InlineData("  A = 1  ", "A", "1")]
    [InlineData("export A=1", "A", "1")]
    [InlineData("A=\"a # b\"", "A", "a # b")]
    [InlineData("A='C:\\Users\\me\\.claude-x'", "A", "C:\\Users\\me\\.claude-x")]
    [InlineData("A=\"quoted\" # comment", "A", "quoted")]
    [InlineData("A=plain # comment", "A", "plain")]
    [InlineData("A=a#b", "A", "a#b")]
    [InlineData("A=", "A", "")]
    [InlineData("_A1=x=y", "_A1", "x=y")]
    public void ParseLine_ReadsDotenv(string line, string key, string value)
    {
        Assert.Equal((key, value), DotEnvFiles.ParseLine(line));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# A=1")]
    public void ParseLine_SkipsBlankLinesAndComments(string line)
    {
        Assert.Null(DotEnvFiles.ParseLine(line));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("=1")]
    [InlineData("1A=1")]
    [InlineData("A B=1")]
    [InlineData("A=\"unclosed")]
    [InlineData("A=\"x\" y")]
    public void ParseLine_RefusesWhatIsNotKeyValue(string line)
    {
        Assert.Null(DotEnvFiles.ParseLine(line)!.Value.Key);
    }

    [Fact]
    public void Read_NearerFileWinsKeyByKey_AndALineThatDoesNotParseIsSkipped()
    {
        var work = ServerProcess.CreateWorkDir("dotenv");
        try
        {
            var root = Path.Combine(work, "roots", "r");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(work, ".env"), "FAR=far\nBOTH=far\nnot a line\n");
            File.WriteAllText(Path.Combine(root, ".env"), "# the root's own\nBOTH=near\r\nNEAR=near\r\n");

            var env = DotEnvFiles.Read(root, NullLogger.Instance)!;

            Assert.Equal("far", env["FAR"]);
            Assert.Equal("near", env["BOTH"]);
            Assert.Equal("near", env["NEAR"]);
            Assert.Equal(3, env.Count);
        }
        finally
        {
            ServerProcess.DeleteWorkDir(work);
        }
    }

    [Theory]
    [InlineData("Authentication__ApiKey")]
    [InlineData("ASPNETCORE_Authentication__ApiKey")]
    [InlineData("DOTNET_authentication__KeyFile")]
    public void Read_DropsTheServersOwnSecrets(string key)
    {
        var work = ServerProcess.CreateWorkDir("dotenv");
        try
        {
            File.WriteAllText(Path.Combine(work, ".env"), $"{key}=leaked\nKEPT=1\n");

            var env = DotEnvFiles.Read(work, NullLogger.Instance)!;

            Assert.False(env.ContainsKey(key));
            Assert.Equal("1", env["KEPT"]);
        }
        finally
        {
            ServerProcess.DeleteWorkDir(work);
        }
    }

    /// <summary>A root with no config.json still gets the .env files' environment, in its one action and its own.</summary>
    [Fact]
    public void RootWithNoConfig_GetsTheDotEnvEnvironment()
    {
        var work = ServerProcess.CreateWorkDir("dotenv");
        try
        {
            File.WriteAllText(Path.Combine(work, ".env"), "CLAUDE_CONFIG_DIR=/accounts/x\n");

            var config = new RootConfigReader(NullLogger<RootConfigReader>.Instance).ReadConfigStrict(work);

            Assert.Equal("/accounts/x", config.ResolveAction(null)!.Environment!["CLAUDE_CONFIG_DIR"]);
            Assert.Equal("/accounts/x", config.Environment!["CLAUDE_CONFIG_DIR"]);
        }
        finally
        {
            ServerProcess.DeleteWorkDir(work);
        }
    }

    /// <summary>
    /// Profile, then .env files, then the root's config.json: each key from the highest layer that sets it, at the
    /// create and, read again, at the resume.
    /// </summary>
    [Fact]
    public async Task Launch_LaysTheDotEnvFilesBetweenTheProfileAndTheRoot_ReadFresh()
    {
        await using var harness = new LifecycleHarness(new FakeScript().EmitInit().AwaitStdin(),
            profileEnvironment: new Dictionary<string, string> { ["PROFILE"] = "profile", ["DOTENV"] = "profile", ["ROOT"] = "profile" });
        // The harness's config.json names the fake's files in its environment: the root's own key goes beside them
        var configPath = Path.Combine(harness.RootPath, ".godmode-root", "config.json");
        var config = JsonNode.Parse(File.ReadAllText(configPath))!;
        config["environment"]!["ROOT"] = "root";
        File.WriteAllText(configPath, config.ToJsonString());
        File.WriteAllText(Path.Combine(harness.RootsDir, ".env"), "DOTENV=far\nROOT=far\nFAR=far\n");
        File.WriteAllText(Path.Combine(harness.RootPath, ".env"), "DOTENV=near\n");

        var created = await harness.CreateProjectAsync();
        var create = await harness.WaitForStdinAsync(created.Id);

        Assert.Equal("profile", create.Environment["PROFILE"]);
        Assert.Equal("near", create.Environment["DOTENV"]);
        Assert.Equal("far", create.Environment["FAR"]);
        Assert.Equal("root", create.Environment["ROOT"]);

        File.WriteAllText(Path.Combine(harness.RootPath, ".env"), "DOTENV=changed\n");
        await harness.Projects.StopProjectAsync(created.Id);
        await harness.WaitForStateAsync(created.Id, ProjectState.Stopped);
        await harness.Projects.ResumeProjectAsync(created.Id);
        var resume = await harness.WaitForLaunchAsync(created.Id, _ => true, index: 1);

        Assert.Equal("changed", resume.Environment["DOTENV"]);
    }
}
