using GodMode.ClientBase.Bridge;

namespace GodMode.Relay.Tests;

/// <summary>The app's own pages, where the WebView stays, and where anything else goes instead.</summary>
public sealed class AppOriginTests
{
    [Theory]
    [InlineData("https://0.0.0.1/")]
    [InlineData("https://0.0.0.1/index.html?server=a#top")]
    [InlineData("HTTPS://0.0.0.1/")]
    [InlineData("https://0.0.0.1:443/")]
    [InlineData("app://0.0.0.1/")]
    public void The_apps_pages_are_its_own(string address)
    {
        Assert.True(AppOrigin.Contains(address));
        Assert.Equal(WebViewDestination.App, AppOrigin.DestinationOf(address));
    }

    [Theory]
    [InlineData("http://0.0.0.1/")]
    [InlineData("https://0.0.0.1:8443/")]
    [InlineData("https://0.0.0.1.example.invalid/")]
    [InlineData("https://user@0.0.0.1/")]
    [InlineData("https://example.invalid/?next=https://0.0.0.1/")]
    [InlineData("https://github.com/johnjuuljensen/GodMode/pull/1")]
    public void An_http_address_anywhere_else_opens_in_the_browser(string address)
    {
        Assert.False(AppOrigin.Contains(address));
        Assert.Equal(WebViewDestination.Browser, AppOrigin.DestinationOf(address));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("about:blank")]
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:someone@example.invalid")]
    [InlineData("intent://scan#Intent;scheme=zxing;end")]
    [InlineData("file:///etc/passwd")]
    [InlineData("app://example.invalid/")]
    [InlineData("src/foo.ts")]
    public void Any_other_address_goes_nowhere(string? address)
    {
        Assert.False(AppOrigin.Contains(address));
        Assert.Equal(WebViewDestination.Nowhere, AppOrigin.DestinationOf(address));
    }
}
