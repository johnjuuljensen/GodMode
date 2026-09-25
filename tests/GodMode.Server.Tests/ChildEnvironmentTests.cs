using GodMode.Server.Services;

namespace GodMode.Server.Tests;

/// <summary>
/// A Claude process and a root script start from an allowlist, not from the server's environment: a
/// secret the server holds, its own API key included, reaches a session or a script only when config names it.
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
        new("Authentication__ApiKey", "the-servers-key"),
        new("AUTHENTICATION__APIKEY", "the-servers-key"),
        new("GODMODE_TEST_SECRET", "canary"),
        new("GITHUB_TOKEN", "ghp-server"),
        new("AWS_SECRET_ACCESS_KEY", "aws-server"),
    ];

    private static ChildEnvironment Of(string child) => child == "claude" ? ChildEnvironment.Claude : ChildEnvironment.Script;

    [Theory]
    [InlineData("claude")]
    [InlineData("script")]
    public void ServerSecrets_AreNotInherited(string child)
    {
        var env = Of(child).Build(ServerEnvironment, configured: null);

        Assert.DoesNotContain("GODMODE_TEST_SECRET", env.Keys);
        Assert.DoesNotContain("GITHUB_TOKEN", env.Keys);
        Assert.DoesNotContain("AWS_SECRET_ACCESS_KEY", env.Keys);
        // The server's own API key, in whatever case
        Assert.DoesNotContain(env, v => v.Key.StartsWith("Authentication", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("the-servers-key", env.Values);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("script")]
    public void Essentials_AreInherited(string child)
    {
        var env = Of(child).Build(ServerEnvironment, configured: null);

        Assert.Equal("/usr/bin", env["PATH"]);
        Assert.Equal("/home/godmode", env["HOME"]);
        Assert.Equal("C.UTF-8", env["LC_ALL"]);
    }

    [Fact]
    public void ClaudeCodesCredentials_ReachClaude_NotScripts()
    {
        Assert.Equal("sk-claude", ChildEnvironment.Claude.Build(ServerEnvironment, configured: null)["ANTHROPIC_API_KEY"]);
        Assert.DoesNotContain("ANTHROPIC_API_KEY", ChildEnvironment.Script.Build(ServerEnvironment, configured: null).Keys);
        Assert.True(ChildEnvironment.Script.AllowedNames.IsSubsetOf(ChildEnvironment.Claude.AllowedNames));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("script")]
    public void EveryVariable_IsAllowlistedOrConfigured(string child)
    {
        var configured = new Dictionary<string, string>
        {
            ["GH_TOKEN"] = "ghp-configured",
            ["GODMODE_PROJECT_ID"] = "proj1",
        };

        var env = Of(child).Build(ServerEnvironment, configured);

        Assert.All(env.Keys, name => Assert.True(
            Of(child).IsAllowed(name) || configured.ContainsKey(name), $"{name} is neither allowlisted nor configured"));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("script")]
    public void ConfiguredVariables_AreAdded_AndWinOverInherited(string child)
    {
        var env = Of(child).Build(ServerEnvironment, new Dictionary<string, string>
        {
            ["GITHUB_TOKEN"] = "ghp-named-in-config",
            ["PATH"] = "/opt/tools",
        });

        Assert.Equal("ghp-named-in-config", env["GITHUB_TOKEN"]);
        Assert.Equal("/opt/tools", env["PATH"]);
    }

    [Fact]
    public void ServerGodModeVariables_AreNotInherited_OnlyTheOnesConfigured()
    {
        var env = ChildEnvironment.Script.Build(
            [new("GODMODE_PROJECT_ID", "stale"), new("GODMODE_ROOT_PATH", "/srv/roots")],
            new Dictionary<string, string> { ["GODMODE_PROJECT_ID"] = "proj1" });

        Assert.Equal("proj1", Assert.Single(env).Value);
    }

    [Fact]
    public void Allowlist_IsCaseInsensitive()
    {
        var env = ChildEnvironment.Claude.Build([new("https_proxy", "http://proxy:3128"), new("SystemRoot", @"C:\Windows")], configured: null);

        Assert.Equal("http://proxy:3128", env["https_proxy"]);
        Assert.Equal(@"C:\Windows", env["SystemRoot"]);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("script")]
    public void CurrentEnvironment_CanaryOnTheServer_DoesNotReachTheChild(string child)
    {
        const string name = "GODMODE_TEST_SECRET";
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, "canary-" + Guid.NewGuid().ToString("N"));
        try
        {
            var env = Of(child).Build(ChildEnvironment.Current(), new Dictionary<string, string> { ["GODMODE_PROJECT_ID"] = "p" });

            Assert.DoesNotContain(name, env.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("PATH", env.Keys, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}
