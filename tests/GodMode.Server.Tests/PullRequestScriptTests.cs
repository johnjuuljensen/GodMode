using GodMode.Server.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Tests;

/// <summary>A status script's output is untrusted: <see cref="PullRequestScript.Parse"/> takes the documented object and refuses the rest.</summary>
public class PullRequestScriptTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    private static string Report(string state = "open", string review = "none", string url = "https://github.com/o/r/pull/12", string number = "12") =>
        $$$"""{"pullRequest": {"url": "{{{url}}}", "number": {{{number}}}, "state": "{{{state}}}", "review": "{{{review}}}"}}""";

    [Theory]
    [InlineData("draft", "none", PullRequestState.Draft, PullRequestReview.None)]
    [InlineData("open", "changes_requested", PullRequestState.Open, PullRequestReview.ChangesRequested)]
    [InlineData("open", "approved", PullRequestState.Open, PullRequestReview.Approved)]
    [InlineData("merged", "approved", PullRequestState.Merged, PullRequestReview.Approved)]
    [InlineData("closed", "none", PullRequestState.Closed, PullRequestReview.None)]
    public void EachState_IsRead(string state, string review, PullRequestState expectedState, PullRequestReview expectedReview)
    {
        var pr = PullRequestScript.Parse(Report(state, review) + "\r\n", Now);

        Assert.Equal(new PullRequestStatus("https://github.com/o/r/pull/12", 12, expectedState, expectedReview, Now), pr);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData(" {}\n")]
    [InlineData("""{"pullRequest": null}""")]
    public void NoPullRequest_IsNull(string output) => Assert.Null(PullRequestScript.Parse(output, Now));

    [Theory]
    [InlineData("", "not one JSON object")]
    [InlineData("no pull request", "not one JSON object")]
    [InlineData("{} {}", "not one JSON object")]
    [InlineData("Running gh...\n{}", "not one JSON object")]
    [InlineData("[]", "not an object")]
    [InlineData("""{"pullRequest": 12}""", "not an object")]
    [InlineData("""{"pullrequest": {}}""", "unknown property 'pullrequest'")]
    [InlineData("""{"pullRequest": null, "pullRequest": null}""", "not one JSON object")]
    [InlineData("""{"pullRequest": {"url": "https://x/1", "number": 1, "state": "open"}}""", "pullRequest.review is missing")]
    [InlineData("""{"pullRequest": {"url": "https://x/1", "number": 1, "state": "open", "review": "none", "title": "x"}}""", "unknown property 'pullRequest.title'")]
    [InlineData("""{"pullRequest": {"url": "https://x/1", "number": "1", "state": "open", "review": "none"}}""", "pullRequest.number is a JSON String")]
    [InlineData("""{"pullRequest": {"url": "https://x/1", "number": 1, "state": "OPEN", "review": "none"}}""", "pullRequest.state 'OPEN' is not one of")]
    [InlineData("""{"pullRequest": {"url": "https://x/1", "number": 1, "state": "open", "review": "REVIEW_REQUIRED"}}""", "pullRequest.review 'REVIEW_REQUIRED'")]
    [InlineData("""{"pullRequest": {"url": "https://x/1", "number": 1, /* c */ "state": "open", "review": "none"}}""", "not one JSON object")]
    public void AnythingElse_IsRefused_SayingWhy(string output, string reason)
    {
        var ex = Assert.Throws<FormatException>(() => PullRequestScript.Parse(output, Now));
        Assert.Contains(reason, ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("1.5")]
    [InlineData("99999999999")]
    public void Number_MustBeAPositiveWholeNumber(string number) =>
        Assert.Contains("not a positive whole number", Assert.Throws<FormatException>(() => PullRequestScript.Parse(Report(number: number), Now)).Message);

    [Theory]
    [InlineData("pull/12")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    public void Url_MustBeHttp(string url) =>
        Assert.Contains("is not an http(s) URL", Assert.Throws<FormatException>(() => PullRequestScript.Parse(Report(url: url), Now)).Message);

    [Fact]
    public void LongOutput_IsRefused_AndNotEchoed()
    {
        var url = "https://x/" + new string('a', 5000);
        var ex = Assert.Throws<FormatException>(() => PullRequestScript.Parse(Report(url: url), Now));
        Assert.True(ex.Message.Length < 300, ex.Message);

        var huge = "{\"pullRequest\": null" + new string(' ', PullRequestScript.MaxOutputChars) + "}";
        Assert.Contains("longer than", Assert.Throws<FormatException>(() => PullRequestScript.Parse(huge, Now)).Message);
    }

    [Fact]
    public void Apply_KeepsWhenItChanged_UntilStateOrReviewChanges()
    {
        var earlier = Now.AddHours(-1);
        var before = new PullRequestStatus("https://x/12", 12, PullRequestState.Open, PullRequestReview.None, earlier);

        Assert.Equal(before, PullRequestScript.Apply(before, before with { ChangedAt = Now }));
        Assert.Equal(before with { Url = "https://y/12" }, PullRequestScript.Apply(before, before with { Url = "https://y/12", ChangedAt = Now }));
        var reviewed = before with { Review = PullRequestReview.ChangesRequested, ChangedAt = Now };
        Assert.Equal(reviewed, PullRequestScript.Apply(before, reviewed));
        var another = before with { Number = 13, ChangedAt = Now };
        Assert.Equal(another, PullRequestScript.Apply(before, another));
        Assert.Null(PullRequestScript.Apply(before, null));
        Assert.Equal(reviewed, PullRequestScript.Apply(null, reviewed));
    }
}
