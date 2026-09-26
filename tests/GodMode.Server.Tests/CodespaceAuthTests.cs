using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text;
using GodMode.Server.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace GodMode.Server.Tests;

/// <summary>
/// Codespace mode through the real authentication handler, with GitHub's <c>/user</c> faked: a token
/// of <c>GITHUB_USER</c> gets in, except the codespace's own <c>GITHUB_TOKEN</c>, which its sessions
/// are given. Each test's tokens are new, so the handler's token cache cannot answer for GitHub.
/// </summary>
public class CodespaceAuthTests
{
    private const string User = "octocat";

    private static string NewToken() => "ghu_" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task TheCodespacesOwnToken_IsRefused_ThoughGitHubWouldSayItIsTheUsers()
    {
        var own = NewToken();
        var github = new FakeGitHub(login: User);

        var result = await AuthenticateAsync(new AuthSettings(AuthMode.Codespace, GitHubUser: User, CodespaceToken: own), github, own);

        Assert.False(result.Succeeded);
        Assert.Contains("GITHUB_TOKEN", result.Failure?.Message);
        Assert.Empty(github.Tokens);
    }

    [Fact]
    public async Task AnotherTokenOfTheUser_IsAccepted()
    {
        var github = new FakeGitHub(login: User);
        var other = NewToken();

        var result = await AuthenticateAsync(new AuthSettings(AuthMode.Codespace, GitHubUser: User, CodespaceToken: NewToken()), github, other);

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.Equal(User, result.Principal!.FindFirstValue(ClaimTypes.Name));
        Assert.Equal([other], github.Tokens);
    }

    [Fact]
    public async Task ATokenOfAnotherUser_IsRefused()
    {
        var github = new FakeGitHub(login: "mallory");

        var result = await AuthenticateAsync(new AuthSettings(AuthMode.Codespace, GitHubUser: User, CodespaceToken: NewToken()), github, NewToken());

        Assert.False(result.Succeeded);
        Assert.Single(github.Tokens);
    }

    [Fact]
    public async Task WithoutACodespaceToken_TheUsersTokensStillGetIn()
    {
        var result = await AuthenticateAsync(new AuthSettings(AuthMode.Codespace, GitHubUser: User), new FakeGitHub(login: User), NewToken());

        Assert.True(result.Succeeded, result.Failure?.Message);
    }

    /// <summary>The handler, registered as the server registers it, authenticating one bearer token on the hub.</summary>
    private static async Task<AuthenticateResult> AuthenticateAsync(AuthSettings settings, FakeGitHub github, string token)
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IHttpClientFactory>(github)
            .AddGodModeAuth(settings)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = GodModeAuthExtensions.HubPath + "/negotiate";
        context.Request.Headers.Authorization = $"Bearer {token}";
        return await context.AuthenticateAsync(GodModeAuthExtensions.SchemeName);
    }

    /// <summary>GitHub's <c>/user</c>, answering <paramref name="login"/> for every token, and keeping the tokens asked about.</summary>
    private sealed class FakeGitHub(string login) : HttpMessageHandler, IHttpClientFactory
    {
        public ConcurrentQueue<string> Tokens { get; } = new();

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://api.github.com/user", request.RequestUri?.ToString());
            Tokens.Enqueue(request.Headers.Authorization?.Parameter ?? "");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"login":"{{login}}"}""", Encoding.UTF8, "application/json"),
            });
        }
    }
}
