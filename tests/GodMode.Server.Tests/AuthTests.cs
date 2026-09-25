using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.Server.Tests;

/// <summary>
/// Authentication and browser origins, exercised against the real server process. Every mode needs a
/// credential, whatever the binding: <c>codespace</c> (CODESPACES=true) beats <c>apikey</c>, whose key is
/// <c>Authentication:ApiKey</c>, else the one the server generates into its key file on its first start.
/// A request with an <c>Origin</c> other than the server's own is refused with 403 before authentication.
/// </summary>
public class AuthTests
{
    private const string ApiKey = "test-api-key-0123456789abcdef";
    private const string HubPath = "/hubs/projects";

    // ── The key: configured, or generated ──

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    public async Task NoConfiguredKey_GeneratesOne_PrintsItOnce_AndRequiresIt_FromLoopbackToo(string host)
    {
        var workDir = ServerProcess.CreateWorkDir("auth");
        var keyFile = ServerProcess.KeyFilePath(workDir);
        try
        {
            string key;
            await using (var first = await StartHealthyAsync(host, apiKey: null, workDir: workDir))
            {
                key = (await File.ReadAllTextAsync(keyFile)).Trim();
                Assert.Matches("^[0-9a-f]{64}$", key);
                Assert.True(await Lifecycle.LifecycleHarness.WaitForAsync(() => Task.FromResult(first.Server.Output.Contains(key))),
                    $"The generated key was not printed.\n{first.Server.Output}");
                Assert.DoesNotContain(key, await ReadLogsAsync(first));

                // Loopback is no exception: the caller here is 127.0.0.1
                await AssertKeyRequiredAsync(first, key);
                using var servers = await first.Http.GetAsync("/servers");
                Assert.Equal(HttpStatusCode.Unauthorized, servers.StatusCode);
                await using var hub = BuildHub(first.BaseUrl, key);
                await hub.StartAsync();
                Assert.Equal(JsonValueKind.Array, (await hub.InvokeAsync<JsonElement>("ListProfiles")).ValueKind);
            }

            // A restart reuses the key, and does not print it again
            await using var restarted = await StartHealthyAsync(host, apiKey: null, workDir: workDir);
            Assert.Equal(key, (await File.ReadAllTextAsync(keyFile)).Trim());
            await AssertKeyRequiredAsync(restarted, key);
            Assert.True(await Lifecycle.LifecycleHarness.WaitForAsync(async () => (await ReadLogsAsync(restarted)).Contains(keyFile)),
                $"The restart did not log that it uses {keyFile}.\n{restarted.Server.Output}");
            Assert.DoesNotContain(key, restarted.Server.Output);
        }
        finally
        {
            ServerProcess.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ConfiguredKey_Wins_AndNoKeyFileIsWritten()
    {
        await using var run = await StartHealthyAsync("127.0.0.1", ApiKey);
        await AssertKeyRequiredAsync(run, ApiKey);
        Assert.False(File.Exists(ServerProcess.KeyFilePath(run.Server.WorkDir)));
    }

    [Fact]
    public async Task Key_NonLoopback_StartsAndRequiresKey()
    {
        await using var run = await StartHealthyAsync("0.0.0.0", ApiKey);
        await AssertKeyRequiredAsync(run, ApiKey);
    }

    // ── Browser origins: the server's own, or none ──

    /// <summary>
    /// A page in the user's browser from anywhere but the server itself, a dev server on another
    /// localhost port included, is refused before authentication: with the key, without it, and on
    /// an anonymous endpoint. The WebSocket upgrade, which CORS does not cover, is refused the same.
    /// </summary>
    [Theory]
    [InlineData("http://localhost:5173")] // the Vite dev server, outside Development
    [InlineData("http://localhost:{other}")]
    [InlineData("http://127.0.0.1:{other}")]
    [InlineData("https://127.0.0.1:{port}")]
    [InlineData("http://rebind.evil.example:{port}")]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    [InlineData("http://127.0.0.1:{port}/path")]
    public async Task ForeignOrigin_Is403_OverHttpAndTheWebSocketUpgrade(string origin)
    {
        await using var run = await StartHealthyAsync("127.0.0.1", ApiKey);
        origin = WithPorts(origin, run);

        using var withKey = await NegotiateAsync(run.Http, ApiKey, origin);
        Assert.Equal(HttpStatusCode.Forbidden, withKey.StatusCode);
        using var withoutKey = await NegotiateAsync(run.Http, token: null, origin);
        Assert.Equal(HttpStatusCode.Forbidden, withoutKey.StatusCode);
        using var health = await SendAsync(run.Http, HttpMethod.Get, "/health", token: null, origin);
        Assert.Equal(HttpStatusCode.Forbidden, health.StatusCode);

        // A raw WebSocket upgrade straight to the hub, with the key, as a cross-site page would open it
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", origin);
        var ex = await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(HubSocketUrl(run, ApiKey), CancellationToken.None));
        Assert.Contains("403", ex.Message);
    }

    [Theory]
    [InlineData("http://127.0.0.1:{port}")]
    [InlineData("http://localhost:{port}")]
    [InlineData("http://[::1]:{port}")]
    public async Task OwnOrigin_GetsThroughWithTheKey_OverHttpAndTheWebSocketUpgrade(string origin)
    {
        await using var run = await StartHealthyAsync("127.0.0.1", ApiKey);
        origin = WithPorts(origin, run);

        using var withKey = await NegotiateAsync(run.Http, ApiKey, origin);
        Assert.Equal(HttpStatusCode.OK, withKey.StatusCode);
        // The origin is no credential
        using var withoutKey = await NegotiateAsync(run.Http, token: null, origin);
        Assert.Equal(HttpStatusCode.Unauthorized, withoutKey.StatusCode);

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", origin);
        await socket.ConnectAsync(HubSocketUrl(run, ApiKey), CancellationToken.None);
        Assert.Equal(WebSocketState.Open, socket.State);

        using var keyless = new ClientWebSocket();
        keyless.Options.SetRequestHeader("Origin", origin);
        var ex = await Assert.ThrowsAsync<WebSocketException>(() => keyless.ConnectAsync(HubSocketUrl(run, token: null), CancellationToken.None));
        Assert.Contains("401", ex.Message);
    }

    /// <summary>Not a browser (the MAUI relay, the app's attention service): no Origin, and the key alone.</summary>
    [Fact]
    public async Task NoOrigin_NeedsTheKeyAlone_OverHttpAndTheWebSocketUpgrade()
    {
        await using var run = await StartHealthyAsync("127.0.0.1", ApiKey);

        await AssertKeyRequiredAsync(run, ApiKey);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(HubSocketUrl(run, ApiKey), CancellationToken.None);
        Assert.Equal(WebSocketState.Open, socket.State);

        using var keyless = new ClientWebSocket();
        var ex = await Assert.ThrowsAsync<WebSocketException>(() => keyless.ConnectAsync(HubSocketUrl(run, token: null), CancellationToken.None));
        Assert.Contains("401", ex.Message);
    }

    /// <summary>An origin the server cannot tell is its own (a reverse proxy's, a host name's) is let through when listed.</summary>
    [Fact]
    public async Task AllowedOrigins_AreLetThrough_AndNoOthers()
    {
        await using var run = await StartHealthyAsync("127.0.0.1", ApiKey, environment: new Dictionary<string, string>
        {
            ["Authentication__AllowedOrigins__0"] = "https://godmode.tailnet.ts.net",
            ["Authentication__AllowedOrigins__1"] = "http://nas.local:8080/",
        });

        foreach (var origin in new[] { "https://godmode.tailnet.ts.net", "http://nas.local:8080" })
        {
            using var negotiate = await NegotiateAsync(run.Http, ApiKey, origin);
            Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Origin", origin);
            await socket.ConnectAsync(HubSocketUrl(run, ApiKey), CancellationToken.None);
            Assert.Equal(WebSocketState.Open, socket.State);
        }

        using var other = await NegotiateAsync(run.Http, ApiKey, "https://other.tailnet.ts.net");
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    }

    [Fact]
    public async Task AllowedOrigins_EntryThatIsNotAnOrigin_FailsAtStartupWithMessage()
    {
        var workDir = ServerProcess.CreateWorkDir("auth");
        using (var server = ServerProcess.Start(workDir, $"http://127.0.0.1:{ServerProcess.GetFreePort()}",
            environment: new Dictionary<string, string> { ["Authentication__AllowedOrigins__0"] = "https://godmode.example/app" }))
        {
            Assert.True(await server.WaitForExitAsync(TimeSpan.FromSeconds(30)), $"Server kept running.\n{server.Output}");
            Assert.NotEqual(0, server.ExitCode);
            Assert.Contains("Authentication:AllowedOrigins", server.Output);
            Assert.Contains("https://godmode.example/app", server.Output);
        }
        ServerProcess.DeleteWorkDir(workDir);
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

    [Fact]
    public async Task QueryToken_IsOnlyAcceptedByTheHub_AndNeverLogged()
    {
        await using var run = await StartHealthyAsync("127.0.0.1", ApiKey);

        // Outside the hub, a key in the URL is refused.
        using var servers = await run.Http.GetAsync($"/servers?access_token={ApiKey}");
        Assert.Equal(HttpStatusCode.Unauthorized, servers.StatusCode);

        // The hub's WebSocket upgrade carries it in the query string, all a browser can do, and works.
        await using (var hub = new HubConnectionBuilder()
            .WithUrl($"{run.BaseUrl}{HubPath}", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(ApiKey);
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
            })
            .Build())
        {
            await hub.StartAsync();
            await hub.InvokeAsync<JsonElement>("ListProfiles");
        }

        // Neither URL reached the console or the log file.
        Assert.DoesNotContain(ApiKey, run.Server.Output);
        var logDir = Path.Combine(run.Server.WorkDir, ".godmode-logs");
        foreach (var logFile in Directory.GetFiles(logDir))
        {
            using var reader = new StreamReader(new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            var log = await reader.ReadToEndAsync();
            Assert.Contains("Authentication mode", log); // the right file, and it is being written
            Assert.DoesNotContain(ApiKey, log);
        }
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

    // ── The MCP endpoint authenticates projects, not users ──

    /// <summary>
    /// Only a project token opens /mcp: not an anonymous caller, not the user's API key, with or
    /// without a project named. <see cref="McpEndpointTests"/> has a project's own token let in,
    /// another project's refused, and a project token refused everywhere else.
    /// </summary>
    [Fact]
    public async Task McpEndpoint_RequiresAProjectToken()
    {
        await using var run = await StartHealthyAsync("127.0.0.1", ApiKey);

        using var anonymous = await PostMcpAsync(run.Http, projectId: null, token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var bogus = await PostMcpAsync(run.Http, projectId: "no-such-project", token: "not-a-project-token");
        Assert.Equal(HttpStatusCode.Unauthorized, bogus.StatusCode);

        // The user's API key is not a project token.
        using var userKey = await PostMcpAsync(run.Http, projectId: null, token: ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, userKey.StatusCode);

        using var userKeyForAProject = await PostMcpAsync(run.Http, projectId: "Default/work/p1", token: ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, userKeyForAProject.StatusCode);
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

        // Codespace mode wins over a configured API key and generates none:
        // neither anonymous access nor the API key gets in.
        Assert.False(File.Exists(ServerProcess.KeyFilePath(run.Server.WorkDir)));
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

    private static async Task AssertKeyRequiredAsync(Run run, string key)
    {
        using var anonymous = await NegotiateAsync(run.Http, token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var wrong = await NegotiateAsync(run.Http, token: key + "-wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        using var right = await NegotiateAsync(run.Http, key);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    /// <summary>The hub's WebSocket URL, with the key as a browser sends it (it cannot set headers on an upgrade).</summary>
    private static Uri HubSocketUrl(Run run, string? token) =>
        new($"{run.BaseUrl.Replace("http", "ws")}{HubPath}{(token == null ? "" : $"?access_token={Uri.EscapeDataString(token)}")}");

    /// <summary><c>{port}</c> is the server's, <c>{other}</c> one it does not listen on.</summary>
    private static string WithPorts(string origin, Run run)
    {
        var port = new Uri(run.BaseUrl).Port;
        return origin.Replace("{port}", $"{port}").Replace("{other}", $"{(port == 65535 ? port - 1 : port + 1)}");
    }

    private static async Task<string> ReadLogsAsync(Run run)
    {
        var logs = new StringBuilder();
        foreach (var logFile in Directory.GetFiles(Path.Combine(run.Server.WorkDir, ".godmode-logs")))
        {
            using var reader = new StreamReader(new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            logs.Append(await reader.ReadToEndAsync());
        }
        return logs.ToString();
    }

    private static Task<HttpResponseMessage> NegotiateAsync(HttpClient http, string? token, string? origin = null) =>
        SendAsync(http, HttpMethod.Post, $"{HubPath}/negotiate?negotiateVersion=1", token, origin);

    private static Task<HttpResponseMessage> PostMcpAsync(HttpClient http, string? projectId, string? token) =>
        http.SendAsync(McpEndpointTests.Initialize(projectId, token));

    private static Task<HttpResponseMessage> SendAsync(HttpClient http, HttpMethod method, string path, string? token, string? origin = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin != null) request.Headers.Add("Origin", origin);
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

    /// <param name="apiKey">The configured key; null for none, so the server generates one.</param>
    /// <param name="workDir">A work directory to reuse (a restart), which the run then leaves in place.</param>
    private static async Task<Run> StartHealthyAsync(
        string host,
        string? apiKey,
        bool writeSpa = false,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workDir = null)
    {
        var ownsWorkDir = workDir == null;
        workDir ??= ServerProcess.CreateWorkDir("auth");
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
        var run = new Run(server, http, baseUrl, ownsWorkDir);
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

    private sealed record Run(ServerProcess Server, HttpClient Http, string BaseUrl, bool OwnsWorkDir) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Http.Dispose();
            Server.Dispose();
            if (OwnsWorkDir) ServerProcess.DeleteWorkDir(Server.WorkDir);
            return ValueTask.CompletedTask;
        }
    }
}
