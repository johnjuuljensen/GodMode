using System.Net;
using GodMode.Server.Auth;
using Microsoft.Extensions.Configuration;

namespace GodMode.Server.Tests;

public class AuthModeSelectorTests
{
    private static readonly string[] Loopback = ["http://127.0.0.1:31337"];
    private static readonly string[] AllInterfaces = ["http://0.0.0.0:31337"];

    [Theory]
    // key × loopback / non-loopback
    [InlineData("key", false, "http://127.0.0.1:31337", AuthMode.ApiKey)]
    [InlineData("key", false, "http://0.0.0.0:31337", AuthMode.ApiKey)]
    // no key × loopback
    [InlineData(null, false, "http://127.0.0.1:31337", AuthMode.Loopback)]
    [InlineData("", false, "http://localhost:31337", AuthMode.Loopback)]
    [InlineData(null, false, "http://[::1]:31337", AuthMode.Loopback)]
    [InlineData(null, false, "http://127.0.0.1:31337;http://localhost:31338", AuthMode.Loopback)]
    // codespace wins over both
    [InlineData(null, true, "http://0.0.0.0:31337", AuthMode.Codespace)]
    [InlineData("key", true, "http://127.0.0.1:31337", AuthMode.Codespace)]
    public void Select_ChoosesMode(string? apiKey, bool isCodespace, string urls, AuthMode expected) =>
        Assert.Equal(expected, AuthModeSelector.Select(apiKey, isCodespace, urls.Split(';')));

    [Theory]
    [InlineData("http://0.0.0.0:31337")]
    [InlineData("http://+:31337")]
    [InlineData("http://*:31337")]
    [InlineData("http://[::]:31337")]
    [InlineData("http://100.101.102.103:31337")]
    [InlineData("http://my-machine.tailnet.ts.net:31337")]
    [InlineData("http://127.0.0.1:31337;http://100.101.102.103:31337")]
    public void Select_NoKeyOnNonLoopback_Throws(string urls)
    {
        var ex = Assert.Throws<AuthConfigurationException>(() => AuthModeSelector.Select(null, false, urls.Split(';')));
        Assert.Contains(AuthModeSelector.ApiKeySetting, ex.Message);
        // The message names the binding that is not loopback.
        Assert.Contains(urls.Split(';')[^1], ex.Message);
    }

    [Fact]
    public void GetConfiguredUrls_ReadsEverySourceKestrelBinds()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Urls"] = "http://127.0.0.1:1; http://localhost:2",
            ["HTTP_PORTS"] = "8080",
            ["Kestrel:Endpoints:Public:Url"] = "http://0.0.0.0:3",
        }).Build();

        Assert.Equal(
            ["http://127.0.0.1:1", "http://localhost:2", "http://*:8080", "http://0.0.0.0:3"],
            AuthModeSelector.GetConfiguredUrls(config));
    }

    [Fact]
    public void GetConfiguredUrls_NothingConfigured_IsKestrelDefaultOnLocalhost()
    {
        var urls = AuthModeSelector.GetConfiguredUrls(new ConfigurationBuilder().Build());
        Assert.Equal(AuthMode.Loopback, AuthModeSelector.Select(null, false, urls));
    }

    [Fact]
    public void Resolve_ReadsKeyCodespaceAndGitHubUserFromConfiguration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Urls"] = AllInterfaces[0],
            ["CODESPACES"] = "true",
            ["GITHUB_USER"] = "octocat",
            [AuthModeSelector.ApiKeySetting] = "ignored-in-codespace",
        }).Build();

        Assert.Equal(new AuthSettings(AuthMode.Codespace, null, "octocat"), AuthModeSelector.Resolve(config));
    }

    [Fact]
    public void Resolve_ApiKeyMode_CarriesTheKey()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Urls"] = Loopback[0],
            [AuthModeSelector.ApiKeySetting] = "secret",
        }).Build();

        Assert.Equal(new AuthSettings(AuthMode.ApiKey, "secret", null), AuthModeSelector.Resolve(config));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.8.9.10", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("10.0.0.5", false)]
    [InlineData("::ffff:10.0.0.5", false)]
    [InlineData("100.101.102.103", false)]
    public void IsLoopback_ChecksTheCallerAddress(string address, bool expected) =>
        Assert.Equal(expected, AuthModeSelector.IsLoopback(IPAddress.Parse(address)));

    [Fact]
    public void IsLoopback_NoAddress_IsNotLoopback() => Assert.False(AuthModeSelector.IsLoopback(null));
}
