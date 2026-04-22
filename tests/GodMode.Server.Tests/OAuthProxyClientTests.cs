using System.Text.Json;
using GodMode.Server.Services;

namespace GodMode.Server.Tests;

public class OAuthProxyClientTests
{
    // Guarantees that the verified_email / email_verified parser treats Google's
    // two wire shapes (bool and string) identically, and that "false" and missing
    // both read as not-verified — the distinction that gates the login.

    [Fact]
    public void TryGetBool_JsonBoolTrue_ReturnsTrue()
    {
        var json = JsonDocument.Parse("""{"email_verified": true}""").RootElement;
        Assert.Equal(true, OAuthProxyClient.TryGetBool(json, "email_verified"));
    }

    [Fact]
    public void TryGetBool_JsonBoolFalse_ReturnsFalse()
    {
        var json = JsonDocument.Parse("""{"email_verified": false}""").RootElement;
        Assert.Equal(false, OAuthProxyClient.TryGetBool(json, "email_verified"));
    }

    [Fact]
    public void TryGetBool_StringTrue_ReturnsTrue()
    {
        var json = JsonDocument.Parse("""{"email_verified": "true"}""").RootElement;
        Assert.Equal(true, OAuthProxyClient.TryGetBool(json, "email_verified"));
    }

    [Fact]
    public void TryGetBool_StringFalse_ReturnsFalse()
    {
        var json = JsonDocument.Parse("""{"email_verified": "false"}""").RootElement;
        Assert.Equal(false, OAuthProxyClient.TryGetBool(json, "email_verified"));
    }

    [Fact]
    public void TryGetBool_MissingProperty_ReturnsNull()
    {
        var json = JsonDocument.Parse("""{"email": "a@b.com"}""").RootElement;
        Assert.Null(OAuthProxyClient.TryGetBool(json, "email_verified"));
    }

    [Fact]
    public void TryGetBool_UnparseableString_ReturnsNull()
    {
        var json = JsonDocument.Parse("""{"email_verified": "yes"}""").RootElement;
        Assert.Null(OAuthProxyClient.TryGetBool(json, "email_verified"));
    }
}
