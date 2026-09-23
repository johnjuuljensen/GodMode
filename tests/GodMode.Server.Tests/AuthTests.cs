using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.Server.Tests;

/// <summary>
/// Auth-mode selection and fail-closed behaviour, exercised against the real server process.
/// Modes: <c>codespace</c> (CODESPACES=true) beats <c>apikey</c> (Authentication:ApiKey set);
/// with neither, unauthenticated use is allowed only when every binding is loopback,
/// and any other binding refuses to start.
/// </summary>
public class AuthTests
{
    private const string ApiKey = "test-api-key-0123456789abcdef";
    private const string HubPath = "/hubs/projects";

    // ── Selection matrix: key / no key × loopback / non-loopback ──

    [Fact]
    public async Task NoKey_Loopback_StartsAndAllowsLocalUse()
    {
        await using var run = await StartHealthyAsync("127.0.0.1");

        using var negotiate = await NegotiateAsync(run.Http, token: null);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);

        await using var hub = BuildHub(run.BaseUrl, token: null);
        await hub.StartAsync();
        var profiles = await hub.InvokeAsync<JsonElement>("ListProfiles");
        Assert.Equal(JsonValueKind.Array, profiles.ValueKind);
    }

    [Fact]
    public async Task NoKey_NonLoopback_FailsAtStartupWithMessage()
    {
        var workDir = ServerProcess.CreateWorkDir("auth");
        var port = ServerProcess.GetFreePort();
        using (var server = ServerProcess.Start(workDir, $"http://0.0.0.0:{port}"))
        {
            var exited = await server.WaitForExitAsync(TimeSpan.FromSeconds(30));

            Assert.True(exited, $"Server kept running on 0.0.0.0 with no API key.\n{server.Output}");
            Assert.NotEqual(0, server.ExitCode);
            Assert.Contains("Authentication:ApiKey", server.Output);
            Assert.Contains("0.0.0.0", server.Output);
        }
        ServerProcess.DeleteWorkDir(workDir);
    }

    [Fact]
    public async Task Key_Loopback_RequiresKey()
    {
        await using var run = await StartHealthyAsync("127.0.0.1", ApiKey);
        await AssertKeyRequiredAsync(run);
    }

    [Fact]
    public async Task Key_NonLoopback_StartsAndRequiresKey()
    {
        await using var run = await StartHealthyAsync("0.0.0.0", ApiKey);
        await AssertKeyRequiredAsync(run);
    }

    // ── The hub ──

    [Fact]
    public async Task AnonymousHubMethodCall_Is401()
    {
        await using var run = await StartHealthyAsync("127.0.0.1", ApiKey);

        // Negotiate is the first request of every hub call; without the key it is refused.
        using var negotiate = await NegotiateAsync(run.Http, token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);

        // A client that skips negotiation (as the MAUI relay does) hits the hub endpoint directly.
        using var direct = await run.Http.GetAsync(HubPath);
        Assert.Equal(HttpStatusCode.Unauthorized, direct.StatusCode);

        // The real SignalR client, calling a real hub method, fails with 401 before the method runs.
        await using var hub = BuildHub(run.BaseUrl, token: null);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => hub.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);

        // With the key as a bearer token (the React client's accessTokenFactory), the same call succeeds.
        await using var authed = BuildHub(run.BaseUrl, ApiKey);
        await authed.StartAsync();
        var profiles = await authed.InvokeAsync<JsonElement>("ListProfiles");
        Assert.Equal(JsonValueKind.Array, profiles.ValueKind);
    }

    // ── Fail closed: only /health and the SPA's static files are anonymous ──

    [Fact]
    public async Task Key_OnlyHealthAndStaticFilesAreAnonymous()
    {
        await using var run = await StartHealthyAsync("127.0.0.1", ApiKey, writeSpa: true);

        foreach (var path in new[] { "/health", "/", "/index.html", "/assets/app.js", "/projects/some-client-route" })
        {
            using var response = await run.Http.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} → {(int)response.StatusCode}, expected 200 anonymously");
        }

        foreach (var path in new[] { "/servers", "/api/status", "/events" })
        {
            using var anonymous = await run.Http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead);
            Assert.True(anonymous.StatusCode == HttpStatusCode.Unauthorized,
                $"GET {path} anonymously → {(int)anonymous.StatusCode}, expected 401");
        }

        // The old anonymous auth-challenge endpoint no longer answers with auth state.
        using var challenge = await run.Http.GetAsync("/api/auth/challenge");
        Assert.True(challenge.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound || IsSpaIndex(challenge),
            $"GET /api/auth/challenge anonymously → {(int)challenge.StatusCode}");

        using var serversWithKey = await SendAsync(run.Http, HttpMethod.Get, "/servers", ApiKey);
        Assert.Equal(HttpStatusCode.OK, serversWithKey.StatusCode);
    }

    [Fact]
    public async Task OAuthRoutesAreGone()
    {
        await using var run = await StartHealthyAsync("127.0.0.1", ApiKey);

        // On the old server these redirected an anonymous caller to an OAuth provider or wrote tokens.
        foreach (var path in new[]
        {
            "/api/oauth/initiate?provider=google&purpose=login",
            "/api/oauth/relay?relay=x&provider=google&state=x",
            "/api/mcp-oauth/initiate?connectorId=x&profileName=Default&mcpServerUrl=https://example.com",
            "/api/mcp-oauth/callback?code=x&state=x",
        })
        {
            using var response = await run.Http.GetAsync(path);
            Assert.False((int)response.StatusCode is >= 300 and < 400,
                $"GET {path} → {(int)response.StatusCode} redirect; the OAuth route should not exist");
            Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound || IsSpaIndex(response),
                $"GET {path} → {(int)response.StatusCode}");
        }
    }

    // ── The MCP bridge's internal API authenticates projects, not users ──

    [Theory]
    [InlineData(null)]
    [InlineData(ApiKey)]
    public async Task InternalApi_RequiresProjectToken(string? apiKey)
    {
        await using var run = await StartHealthyAsync("127.0.0.1", apiKey);

        using var anonymous = await PostInternalStatusAsync(run.Http, projectId: null, token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var bogus = await PostInternalStatusAsync(run.Http, projectId: "no-such-project", token: "not-a-project-token");
        Assert.Equal(HttpStatusCode.Unauthorized, bogus.StatusCode);

        // The user's API key is not a project token.
        if (apiKey != null)
        {
            using var userKey = await PostInternalStatusAsync(run.Http, projectId: null, token: apiKey);
            Assert.Equal(HttpStatusCode.Unauthorized, userKey.StatusCode);
        }
    }

    // ── Codespace mode (CODESPACES=true, token checked against GitHub for GITHUB_USER) ──

    [Theory]
    [InlineData("0.0.0.0", null)]
    [InlineData("127.0.0.1", null)]
    [InlineData("0.0.0.0", ApiKey)]
    public async Task Codespace_StartsOnAnyBinding_AndRequiresGitHubToken(string host, string? apiKey)
    {
        var codespace = new Dictionary<string, string>
        {
            ["CODESPACES"] = "true",
            ["GITHUB_USER"] = "godmode-test-user",
        };
        await using var run = await StartHealthyAsync(host, apiKey, environment: codespace);

        // Codespace mode wins over the loopback escape hatch and over a configured API key:
        // neither anonymous access nor the API key gets in.
        using var anonymous = await NegotiateAsync(run.Http, token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var bogus = await NegotiateAsync(run.Http, token: "not-a-github-token");
        Assert.Equal(HttpStatusCode.Unauthorized, bogus.StatusCode);

        if (apiKey != null)
        {
            using var withKey = await NegotiateAsync(run.Http, apiKey);
            Assert.Equal(HttpStatusCode.Unauthorized, withKey.StatusCode);
        }
    }

    // ── Helpers ──

    private static async Task AssertKeyRequiredAsync(Run run)
    {
        using var anonymous = await NegotiateAsync(run.Http, token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var wrong = await NegotiateAsync(run.Http, token: ApiKey + "-wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        using var right = await NegotiateAsync(run.Http, ApiKey);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    private static Task<HttpResponseMessage> NegotiateAsync(HttpClient http, string? token) =>
        SendAsync(http, HttpMethod.Post, $"{HubPath}/negotiate?negotiateVersion=1", token);

    private static Task<HttpResponseMessage> PostInternalStatusAsync(HttpClient http, string? projectId, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/internal/status")
        {
            Content = new StringContent("""{"message":"hello"}""", Encoding.UTF8, "application/json")
        };
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (projectId != null) request.Headers.Add("X-GodMode-Project-Id", projectId);
        return http.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient http, HttpMethod method, string path, string? token)
    {
        var request = new HttpRequestMessage(method, path);
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    private static HubConnection BuildHub(string baseUrl, string? token) =>
        new HubConnectionBuilder()
            .WithUrl($"{baseUrl}{HubPath}", options =>
            {
                if (token != null) options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

    private const string SpaMarker = "<!-- godmode-test-spa -->";

    private static bool IsSpaIndex(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.OK
        && response.Content.Headers.ContentType?.MediaType == "text/html"
        && response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains(SpaMarker);

    private static async Task<Run> StartHealthyAsync(
        string host,
        string? apiKey = null,
        bool writeSpa = false,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var workDir = ServerProcess.CreateWorkDir("auth");
        if (writeSpa)
        {
            // The server's web root is {contentRoot}/wwwroot, and the content root is the work directory.
            Directory.CreateDirectory(Path.Combine(workDir, "wwwroot", "assets"));
            File.WriteAllText(Path.Combine(workDir, "wwwroot", "index.html"), $"<!doctype html>{SpaMarker}<div id=root></div>");
            File.WriteAllText(Path.Combine(workDir, "wwwroot", "assets", "app.js"), "console.log('spa');");
        }

        var port = ServerProcess.GetFreePort();
        var server = ServerProcess.Start(workDir, $"http://{host}:{port}", apiKey, environment);
        // Always connect over loopback; a 0.0.0.0 binding accepts it too.
        var baseUrl = $"http://127.0.0.1:{port}";
        // Redirects are asserted on, never followed (an OAuth redirect would leave the machine).
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(10),
        };
        var run = new Run(server, http, baseUrl);
        try
        {
            await server.WaitForHealthyAsync(http);
            return run;
        }
        catch
        {
            await run.DisposeAsync();
            throw;
        }
    }

    private sealed record Run(ServerProcess Server, HttpClient Http, string BaseUrl) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Http.Dispose();
            Server.Dispose();
            ServerProcess.DeleteWorkDir(Server.WorkDir);
            return ValueTask.CompletedTask;
        }
    }
}
