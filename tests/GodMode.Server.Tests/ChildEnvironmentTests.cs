using GodMode.Server.Services;

namespace GodMode.Server.Tests;

/// <summary>
/// A Claude process starts from an allowlist, not from the server's environment: a secret the
/// server holds reaches the session only when config names it.
/// </summary>
[Collection(Lifecycle.ServerEnvironmentCollection.Name)]
public class ChildEnvironmentTests
{
    private static readonly KeyValuePair<string, string?>[] ServerEnvironment =
    [
        new("PATH", "/usr/bin"),
        new("HOME", "/home/godmode"),
        new("LC_ALL", "C.UTF-8"),
        new("ANTHROPIC_API_KEY", "sk-claude"),
        new("GODMODE_TEST_SECRET", "canary"),
        new("GITHUB_TOKEN", "ghp-server"),
        new("AWS_SECRET_ACCESS_KEY", "aws-server"),
    ];

    [Fact]
    public void ServerSecrets_AreNotInherited()
    {
        var env = ChildEnvironment.Build(ServerEnvironment, configured: null);

        Assert.DoesNotContain("GODMODE_TEST_SECRET", env.Keys);
        Assert.DoesNotContain("GITHUB_TOKEN", env.Keys);
        Assert.DoesNotContain("AWS_SECRET_ACCESS_KEY", env.Keys);
    }

    [Fact]
    public void AllowlistedVariables_AreInherited()
    {
        var env = ChildEnvironment.Build(ServerEnvironment, configured: null);

        Assert.Equal("/usr/bin", env["PATH"]);
        Assert.Equal("/home/godmode", env["HOME"]);
        Assert.Equal("C.UTF-8", env["LC_ALL"]);
        Assert.Equal("sk-claude", env["ANTHROPIC_API_KEY"]);
    }

    [Fact]
    public void EveryVariable_IsAllowlistedOrConfigured()
    {
        var configured = new Dictionary<string, string>
        {
            ["GH_TOKEN"] = "ghp-configured",
            ["GODMODE_PROJECT_ID"] = "proj1",
        };

        var env = ChildEnvironment.Build(ServerEnvironment, configured);

        Assert.All(env.Keys, name => Assert.True(
            ChildEnvironment.IsAllowed(name) || configured.ContainsKey(name), $"{name} is neither allowlisted nor configured"));
    }

    [Fact]
    public void ConfiguredVariables_AreAdded_AndWinOverInherited()
    {
        var env = ChildEnvironment.Build(ServerEnvironment, new Dictionary<string, string>
        {
            ["GITHUB_TOKEN"] = "ghp-named-in-config",
            ["PATH"] = "/opt/tools",
            ["GODMODE_PROJECT_TOKEN"] = "token",
        });

        Assert.Equal("ghp-named-in-config", env["GITHUB_TOKEN"]);
        Assert.Equal("/opt/tools", env["PATH"]);
        Assert.Equal("token", env["GODMODE_PROJECT_TOKEN"]);
    }

    [Fact]
    public void ServerGodModeVariables_AreNotInherited_OnlyTheOnesSetForTheLaunch()
    {
        var env = ChildEnvironment.Build(
            [new("GODMODE_PROJECT_ID", "stale"), new("GODMODE_MCP_BRIDGE_PATH", "/srv/bridge.js")],
            new Dictionary<string, string> { ["GODMODE_PROJECT_ID"] = "proj1" });

        Assert.Equal("proj1", Assert.Single(env).Value);
    }

    [Fact]
    public void Allowlist_IsCaseInsensitive()
    {
        var env = ChildEnvironment.Build([new("https_proxy", "http://proxy:3128"), new("SystemRoot", @"C:\Windows")], configured: null);

        Assert.Equal("http://proxy:3128", env["https_proxy"]);
        Assert.Equal(@"C:\Windows", env["SystemRoot"]);
    }

    [Fact]
    public void CurrentEnvironment_CanaryOnTheServer_DoesNotReachTheChild()
    {
        const string name = "GODMODE_TEST_SECRET";
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, "canary-" + Guid.NewGuid().ToString("N"));
        try
        {
            var env = ChildEnvironment.Build(ChildEnvironment.Current(), new Dictionary<string, string> { ["GODMODE_PROJECT_ID"] = "p" });

            Assert.DoesNotContain(name, env.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("PATH", env.Keys, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}
