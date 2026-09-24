using GodMode.Server.Services;

namespace GodMode.Server.Tests;

/// <summary>The URL the MCP bridge is given is one this machine reaches the server on.</summary>
public class BridgeUrlTests
{
    [Theory]
    [InlineData(new string[0], "http://127.0.0.1:31337")]
    [InlineData(new[] { "http://127.0.0.1:31337" }, "http://127.0.0.1:31337")]
    [InlineData(new[] { "http://localhost:6000" }, "http://localhost:6000")]
    [InlineData(new[] { "http://[::1]:6001" }, "http://[::1]:6001")]
    // A Tailscale address and a loopback one: loopback
    [InlineData(new[] { "http://100.64.0.1:31337", "http://127.0.0.1:4000" }, "http://127.0.0.1:4000")]
    // Wildcards, as configured (Docker's http://+:31337) and as Kestrel reports them bound
    [InlineData(new[] { "http://+:31337" }, "http://127.0.0.1:31337")]
    [InlineData(new[] { "http://*:31337" }, "http://127.0.0.1:31337")]
    [InlineData(new[] { "http://0.0.0.0:5000" }, "http://127.0.0.1:5000")]
    [InlineData(new[] { "http://[::]:5000" }, "http://127.0.0.1:5000")]
    [InlineData(new[] { "http://myhost:5002" }, "http://127.0.0.1:5002")]
    // Only a non-loopback IP (Tailscale only): that IP, since nothing listens on localhost
    [InlineData(new[] { "http://100.64.0.1:31337" }, "http://100.64.0.1:31337")]
    [InlineData(new[] { "http://100.64.0.1:31337", "http://0.0.0.0:7000" }, "http://127.0.0.1:7000")]
    // Only https: that binding, not a guessed http port
    [InlineData(new[] { "https://127.0.0.1:8443" }, "https://127.0.0.1:8443")]
    [InlineData(new[] { "https://127.0.0.1:8443", "http://100.64.0.1:8080" }, "http://100.64.0.1:8080")]
    // Port 0 is not an address anyone listens on; the bound one is
    [InlineData(new[] { "http://127.0.0.1:0" }, "http://127.0.0.1:31337")]
    [InlineData(new[] { "http://127.0.0.1:0", "http://127.0.0.1:55123" }, "http://127.0.0.1:55123")]
    public void From_IsAnAddressThisMachineReachesTheServerOn(string[] addresses, string expected) =>
        Assert.Equal(expected, BridgeUrl.From(addresses));
}
