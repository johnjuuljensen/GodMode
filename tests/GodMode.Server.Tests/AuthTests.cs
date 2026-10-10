using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.Server.Tests;

/// <summary>
/// Authentication and origins, exercised against the real server process. Every mode needs a credential,
/// whatever the binding: <c>codespace</c> (CODESPACES=true) beats <c>apikey</c>, whose key is
/// <c>Authentication:ApiKey</c>, else the one the server generates into its key file on its first start.
/// No browser is a client: the server serves no page, and a request with any <c>Origin</c> is refused
/// with 403 before authentication.
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
                using var status = await first.Http.GetAsync("/");
                Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
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

    // ── Origins: none is accepted ──

    /// <summary>
    /// Whatever a request's <c>Origin</c> names, the server's own binding and the app's WebView included, it is
    /// refused before authentication: with the key, without it, and on an anonymous endpoint.
    /// </summary>
    [Theory]
    [MemberData(nameof(Origins))]
    public async Task AnyOrigin_Is403_OverHttp(string origin)
    {
        await using var run = await StartHealthyAsync("127.0.0.1", ApiKey);
        origin = WithPort(origin, run);

        using var withKey = await NegotiateAsync(run.Http, ApiKey, origin);
        Assert.Equal(HttpStatusCode.Forbidden, withKey.StatusCode);
        using var withoutKey = await NegotiateAsync(run.Http, token: null, origin);
        Assert.Equal(HttpStatusCode.Forbidden, withoutKey.StatusCode);
        using var status = await SendAsync(run.Http, HttpMethod.Get, "/", ApiKey, origin);
        Assert.Equal(HttpStatusCode.Forbidden, status.StatusCode);
        using var health = await SendAsync(run.Http, HttpMethod.Get, "/health", token: null, origin);
        Assert.Equal(HttpStatusCode.Forbidden, health.StatusCode);
    }

    /// <summary>The hub's WebSocket upgrade, which CORS does not cover, opened with the key as a page would open it.</summary>
    [Theory]
    [MemberData(nameof(Origins))]
    public async Task AnyOrigin_Is403_OnTheWebSocketUpgrade(string origin)
    {
        await using var run = await StartHealthyAsync("127.0.0.1", ApiKey);
        origin = WithPort(origin, run);

        using var socket = HubSocket(ApiKey);
        socket.Options.SetRequestHeader("Origin", origin);
        var ex = await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(HubSocketUrl(run), CancellationToken.None));
        Assert.Contains("403", ex.Message);
    }

    /// <summary>
    /// An <c>Origin</c> the client libraries would not send as such, written on the wire: empty, twice, in lower
    /// or upper case. Each is refused, on an anonymous endpoint and on the hub with the key.
    /// </summary>
    [Theory]
    [InlineData("Origin: ")]
    [InlineData("Origin: https://0.0.0.1\r\nOrigin: https://evil.example")]
    [InlineData("origin: https://evil.example")]
    [InlineData("ORIGIN: null")]
    public async Task AnOriginHeader_EmptyRepeatedOrInAnyCase_Is403(string originLines)
    {
        await using var run = await StartHealthyAsync("127.0.0.1", ApiKey);

        Assert.Equal(200, await RawStatusAsync(run, "GET /health", headerLines: null));
        Assert.Equal(403, await RawStatusAsync(run, "GET /health", originLines));
        Assert.Equal(403, await RawStatusAsync(run, $"POST {HubPath}/negotiate?negotiateVersion=1", $"Authorization: Bearer {ApiKey}\r\n{originLines}"));
    }

    public static TheoryData<string> Origins =>
    [
        // The server's own binding, as a page it served would have sent it
        "http://127.0.0.1:{port}",
        "http://localhost:{port}",
        "http://[::1]:{port}",
        // The app's WebView: its page reaches servers only through the app's relay
        "https://0.0.0.1",
        "http://localhost:5173", // the Vite dev server
        "https://evil.example",
        "null",
    ];

    /// <summary>
    /// Nothing lets an origin in any more: not <c>Authentication:AllowedOrigins</c>, which is no setting now
    /// and stops no start, and not Development (the Vite dev server). A codespace's forwarded port: <see cref="Codespace_StartsOnAnyBinding_AndRequiresGitHubToken"/>.
    /// </summary>
    [Fact]
    public async Task NoConfigurationLetsAnOriginIn()
    {
        await using var run = await StartHealthyAsync("127.0.0.1", ApiKey, environment: new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["Authentication__AllowedOrigins__0"] = "https://godmode.tailnet.ts.net",
            ["Authentication__AllowedOrigins__1"] = "not an origin",
        });

        foreach (var origin in new[] { "https://godmode.tailnet.ts.net", "http://localhost:5173" })
        {
            using var negotiate = await NegotiateAsync(run.Http, ApiKey, origin);
            Assert.True(negotiate.StatusCode == HttpStatusCode.Forbidden, $"{origin} → {(int)negotiate.StatusCode}, expected 403");
        }
    }

    /// <summary>Not a browser (the MAUI relay, the app's attention service): no Origin, and the key alone reaches the hub.</summary>
    [Fact]
    public async Task NoOrigin_NeedsTheKeyAlone_OverHttpAndTheWebSocketUpgrade()
    {
        await using var run = await StartHealthyAsync("127.0.0.1", ApiKey);

        await AssertKeyRequiredAsync(run, ApiKey);

        using var socket = HubSocket(ApiKey);
        await socket.ConnectAsync(HubSocketUrl(run), CancellationToken.None);
        Assert.Equal(WebSocketState.Open, socket.State);

        using var keyless = HubSocket(token: null);
        var ex = await Assert.ThrowsAsync<WebSocketException>(() => keyless.ConnectAsync(HubSocketUrl(run), CancellationToken.None));
        Assert.Contains("401", ex.Message);

        // As the relay connects: a hub method answers
        await using var hub = BuildHub(run.BaseUrl, ApiKey);
        await hub.StartAsync();
        Assert.Equal(JsonValueKind.Array, (await hub.InvokeAsync<JsonElement>("ListProfiles")).ValueKind);
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

    /// <summary>
    /// A key in the URL opens nothing, the hub included: <c>?access_token=</c> was for browsers, which cannot set
    /// headers on a WebSocket upgrade and are no clients. The .NET SignalR client, as the app's relay and attention
    /// service connect, sends the key in the <c>Authorization</c> header, on the upgrade too.
    /// </summary>
    [Fact]
    public async Task QueryToken_IsRefusedEverywhere_TheHubIncluded_AndNeverLogged()
    {
        await using var run = await StartHealthyAsync("127.0.0.1", ApiKey);
        var query = $"access_token={Uri.EscapeDataString(ApiKey)}";

        using var status = await run.Http.GetAsync($"/?{query}");
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
        using var negotiate = await run.Http.PostAsync($"{HubPath}/negotiate?negotiateVersion=1&{query}", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
        using (var socket = HubSocket(token: null))
        {
            var ex = await Assert.ThrowsAsync<WebSocketException>(() =>
                socket.ConnectAsync(new Uri($"{HubSocketUrl(run)}?{query}"), CancellationToken.None));
            Assert.Contains("401", ex.Message);
        }

        // The relay's connection: WebSockets alone, no negotiate, the key from AccessTokenProvider
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

    // ── No page: only /health is anonymous, and / says what the server is ──

    /// <summary>
    /// The server serves no page, even with a built client where its web root would be: /, /index.html, a
    /// bundle file and a client route are 401 without the key, and with it / says what the server is (the
    /// app's codespace probe reads it) while the rest are 404. The browser client's endpoints are gone.
    /// </summary>
    [Fact]
    public async Task NoPathServesThePage_AndOnlyHealthIsAnonymous()
    {
        await using var run = await StartHealthyAsync("127.0.0.1", ApiKey, writeSpa: true);

        using (var health = await run.Http.GetAsync("/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        string[] pagePaths = ["/", "/index.html", "/assets/app.js", "/projects/some-client-route"];
        string[] browserEndpoints = ["/servers", "/events", "/api/status"];
        foreach (var path in pagePaths.Concat(browserEndpoints).Append("/api/auth/challenge"))
        {
            using var anonymous = await run.Http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead);
            Assert.True(anonymous.StatusCode == HttpStatusCode.Unauthorized,
                $"GET {path} anonymously → {(int)anonymous.StatusCode}, expected 401");
        }

        foreach (var path in pagePaths.Skip(1).Concat(browserEndpoints))
        {
            using var withKey = await SendAsync(run.Http, HttpMethod.Get, path, ApiKey);
            Assert.True(withKey.StatusCode == HttpStatusCode.NotFound, $"GET {path} with the key → {(int)withKey.StatusCode}, expected 404");
        }

        using var status = await SendAsync(run.Http, HttpMethod.Get, "/", ApiKey);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("application/json", status.Content.Headers.ContentType?.MediaType);
        var body = await status.Content.ReadAsStringAsync();
        Assert.DoesNotContain(SpaMarker, body);
        Assert.Equal("GodMode.Server", JsonDocument.Parse(body).RootElement.GetProperty("service").GetString());
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
            Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound,
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
            ["CODESPACE_NAME"] = "fuzzy-train-g4x",
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

        // Its forwarded port is no browser's way in either
        using var forwarded = await NegotiateAsync(run.Http, token: null, $"https://fuzzy-train-g4x-{new Uri(run.BaseUrl).Port}.app.github.dev");
        Assert.Equal(HttpStatusCode.Forbidden, forwarded.StatusCode);
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

    /// <summary>The hub's WebSocket URL.</summary>
    private static Uri HubSocketUrl(Run run) => new($"{run.BaseUrl.Replace("http", "ws")}{HubPath}");

    /// <summary>A WebSocket for the hub's upgrade, presenting <paramref name="token"/> in the Authorization header.</summary>
    private static ClientWebSocket HubSocket(string? token)
    {
        var socket = new ClientWebSocket();
        if (token != null) socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        return socket;
    }

    /// <summary>
    /// The status code of <paramref name="requestLine"/> (<c>METHOD path</c>) sent as written, with
    /// <paramref name="headerLines"/> (CRLF-separated) as they are: no client library normalizes them.
    /// </summary>
    private static async Task<int> RawStatusAsync(Run run, string requestLine, string? headerLines)
    {
        var uri = new Uri(run.BaseUrl);
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(uri.Host, uri.Port);
        var stream = tcp.GetStream();
        var request = $"{requestLine} HTTP/1.1\r\nHost: {uri.Authority}\r\nContent-Length: 0\r\nConnection: close\r\n"
            + (headerLines == null ? "" : headerLines + "\r\n") + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var statusLine = await reader.ReadLineAsync() ?? "";
        return int.Parse(statusLine.Split(' ')[1]);
    }

    /// <summary><c>{port}</c> is the server's.</summary>
    private static string WithPort(string origin, Run run) => origin.Replace("{port}", $"{new Uri(run.BaseUrl).Port}");

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
            // A built client where the server's web root would be: {contentRoot}/wwwroot, the content root being the work directory.
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
            Timeout = TestTimeouts.Request,
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
