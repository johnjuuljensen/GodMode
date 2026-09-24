using GodMode.ClientBase.Attention;

namespace GodMode.Relay.Tests;

/// <summary>The deep link a notification opens: both IDs opaque, a project ID's '/' included.</summary>
public sealed class AttentionLinkTests
{
    [Theory]
    [InlineData("3f2b6c1e-8d4a-4b8e-9d7c-2a1b3c4d5e6f", "Default/godmode/feature-178")]
    [InlineData("glorious-space-train-7x9", "Work/root/a:b c%d?e#f")]
    public void A_link_comes_back_from_its_uri_as_it_went_in(string serverId, string projectId)
    {
        var link = new AttentionLink(serverId, projectId);

        Assert.Equal(link, AttentionLink.FromUri(link.ToUri().ToString()));
        Assert.Equal(link, AttentionLink.FromKey(link.Key));
    }

    [Fact]
    public void A_project_id_with_slashes_is_one_segment_of_the_uri()
    {
        var uri = new AttentionLink("server", "Default/root/name").ToUri();

        Assert.Equal("godmode://attention/server:Default%2Froot%2Fname", uri.ToString());
        Assert.Single(uri.AbsolutePath.TrimStart('/').Split('/'));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://attention/server:project")]
    [InlineData("godmode://inbox/server:project")]
    [InlineData("godmode://attention/server:Default/root/name")]
    [InlineData("godmode://attention/server")]
    [InlineData("godmode://attention/:project")]
    [InlineData("godmode://attention/a:b:c")]
    public void Anything_else_names_no_item(string? uri) =>
        Assert.Null(AttentionLink.FromUri(uri));

    [Fact]
    public void The_same_project_on_two_servers_has_two_keys() =>
        Assert.NotEqual(new AttentionLink("alpha", "Default/root/p").Key, new AttentionLink("beta", "Default/root/p").Key);
}
