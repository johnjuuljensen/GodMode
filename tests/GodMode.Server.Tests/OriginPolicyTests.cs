using GodMode.Server.Auth;
using Microsoft.Extensions.Configuration;

namespace GodMode.Server.Tests;

/// <summary>
/// Which browser origins are the server's own, from the addresses it listens on and its configuration.
/// <see cref="AuthTests"/> has a real server refuse the others, over HTTP and on the WebSocket upgrade.
/// </summary>
public class OriginPolicyTests
{
    private static OriginPolicy Policy(bool isDevelopment = false, bool isCodespace = false, params (string Key, string Value)[] settings) =>
        OriginPolicy.From(
            new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value)).Build(),
            isDevelopment, isCodespace);

    [Theory]
    [InlineData("http://127.0.0.1:31337", "http://127.0.0.1:31337 http://localhost:31337 http://[::1]:31337")]
    [InlineData("http://localhost:31337", "http://127.0.0.1:31337 http://localhost:31337 http://[::1]:31337")]
    [InlineData("http://[::1]:31337", "http://127.0.0.1:31337 http://localhost:31337 http://[::1]:31337")]
    [InlineData("https://127.0.0.1:8443", "https://127.0.0.1:8443 https://localhost:8443 https://[::1]:8443")]
    // A wildcard listens on loopback too; what else it is reached by, it cannot tell
    [InlineData("http://+:31337", "http://127.0.0.1:31337 http://localhost:31337 http://[::1]:31337")]
    [InlineData("http://0.0.0.0:31337", "http://127.0.0.1:31337 http://localhost:31337 http://[::1]:31337")]
    [InlineData("http://[::]:31337", "http://127.0.0.1:31337 http://localhost:31337 http://[::1]:31337")]
    // An IP bound alone is only that
    [InlineData("http://100.101.102.103:31337", "http://100.101.102.103:31337")]
    // Kestrel binds a host name to every address
    [InlineData("http://GodMode-Box:31337", "http://127.0.0.1:31337 http://godmode-box:31337 http://localhost:31337 http://[::1]:31337")]
    public void OwnOrigins_AreEachAddressItListensOn(string address, string expected) =>
        Assert.Equal(expected.Split(' ').Order(StringComparer.Ordinal), Policy().AllowedFor([address]).Order(StringComparer.Ordinal));

    [Fact]
    public void OwnOrigins_CoverEveryAddress_AndSkipPipes()
    {
        var allowed = Policy().AllowedFor(["http://127.0.0.1:31337", "http://100.101.102.103:31337", "http://unix:/tmp/godmode.sock"]);

        Assert.Contains("http://100.101.102.103:31337", allowed);
        Assert.Contains("http://localhost:31337", allowed);
        Assert.Equal(4, allowed.Count);
    }

    [Theory]
    [InlineData("http://localhost:5173", "http://localhost:5173")]
    [InlineData("HTTP://LocalHost:5173", "http://localhost:5173")]
    [InlineData("https://godmode.example", "https://godmode.example:443")]
    [InlineData("http://godmode.example/", "http://godmode.example:80")]
    [InlineData("http://[::1]:31337", "http://[::1]:31337")]
    [InlineData("null", null)]
    [InlineData("", null)]
    [InlineData("file://", null)]
    [InlineData("chrome-extension://abcdef", null)]
    [InlineData("http://localhost:5173/app", null)]
    [InlineData("http://localhost:5173?x=1", null)]
    [InlineData("http://user@localhost:5173", null)]
    public void Normalize_KeepsOnlyHttpOrigins(string origin, string? expected) =>
        Assert.Equal(expected, OriginPolicy.Normalize(origin));

    [Fact]
    public void AllowedOrigins_FromAList_OrOneSemicolonSeparatedString()
    {
        var fromList = Policy(settings:
        [
            ($"{OriginPolicy.AllowedOriginsSetting}:0", "https://godmode.tailnet.ts.net/"),
            ($"{OriginPolicy.AllowedOriginsSetting}:1", "http://NAS.local:8080"),
        ]).AllowedFor([]);
        var fromString = Policy(settings: (OriginPolicy.AllowedOriginsSetting, "https://godmode.tailnet.ts.net; http://nas.local:8080")).AllowedFor([]);

        Assert.Equal(["http://nas.local:8080", "https://godmode.tailnet.ts.net:443"], fromList.Order(StringComparer.Ordinal));
        Assert.Equal(fromList.Order(StringComparer.Ordinal), fromString.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("https://godmode.example/app")]
    [InlineData("godmode.example")]
    [InlineData("null")]
    public void AllowedOrigins_EntryThatIsNotAnOrigin_Throws(string entry)
    {
        var ex = Assert.Throws<StartupConfigurationException>(() => Policy(settings: ($"{OriginPolicy.AllowedOriginsSetting}:0", entry)));

        Assert.Contains(OriginPolicy.AllowedOriginsSetting, ex.Message);
        Assert.Contains(entry, ex.Message);
    }

    [Fact]
    public void ViteOrigin_OnlyInDevelopment()
    {
        Assert.DoesNotContain("http://localhost:5173", Policy(isDevelopment: false).AllowedFor(["http://127.0.0.1:31337"]));
        Assert.Contains("http://localhost:5173", Policy(isDevelopment: true).AllowedFor(["http://127.0.0.1:31337"]));
    }

    [Fact]
    public void Codespace_AlsoAllowsItsForwardedPort()
    {
        (string, string)[] codespace = [("CODESPACE_NAME", "fuzzy-train-g4x"), ("GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN", "app.github.dev")];

        Assert.Contains("https://fuzzy-train-g4x-31337.app.github.dev:443",
            Policy(isCodespace: true, settings: codespace).AllowedFor(["http://127.0.0.1:31337"]));
        // Not outside codespace mode, whatever the environment says
        Assert.DoesNotContain("https://fuzzy-train-g4x-31337.app.github.dev:443",
            Policy(isCodespace: false, settings: codespace).AllowedFor(["http://127.0.0.1:31337"]));
    }
}
