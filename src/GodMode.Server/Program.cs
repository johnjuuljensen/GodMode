using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using GodMode.AI;
using GodMode.Server.Auth;
using GodMode.Server.Hubs;
using GodMode.Server.Models;
using GodMode.Server.Services;
using GodMode.Shared;
using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) =>
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(".godmode-logs", "server-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 31));

// Detect auth mode (exactly one, first match wins)
var googleAllowedEmail = builder.Configuration["Authentication:Google:AllowedEmail"];
var isCodespace = string.Equals(
    Environment.GetEnvironmentVariable("CODESPACES"), "true", StringComparison.OrdinalIgnoreCase);
var apiKey = builder.Configuration["Authentication:ApiKey"];

var authMode = !string.IsNullOrEmpty(googleAllowedEmail) ? "google"
    : isCodespace                                         ? "codespace"
    : !string.IsNullOrEmpty(apiKey)                       ? "apikey"
    :                                                       "none";

// Add services to the container
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        var defaults = JsonDefaults.Options;
        options.PayloadSerializerOptions.PropertyNamingPolicy = defaults.PropertyNamingPolicy;
        options.PayloadSerializerOptions.DefaultIgnoreCondition = defaults.DefaultIgnoreCondition;
        foreach (var converter in defaults.Converters)
            options.PayloadSerializerOptions.Converters.Add(converter);
    });

builder.Services.ConfigureHttpJsonOptions(options =>
{
    var defaults = JsonDefaults.Options;
    options.SerializerOptions.PropertyNamingPolicy = defaults.PropertyNamingPolicy;
    options.SerializerOptions.DefaultIgnoreCondition = defaults.DefaultIgnoreCondition;
    foreach (var converter in defaults.Converters)
        options.SerializerOptions.Converters.Add(converter);
});

// CORS: not needed in production (React is same-origin, MAUI proxy is server-to-server).
// Only allow cross-origin in development (vite dev server on a different port).
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials()
                  .WithOrigins("http://localhost:5173", "https://localhost:5173");
        });
    });
}

builder.Services.AddHttpClient();

switch (authMode)
{
    case "google":
        builder.Services.AddGoogleAuth(builder.Configuration);
        break;
    case "codespace" or "apikey":
        builder.Services.AddAuthentication(GodModeAuthExtensions.SchemeName).AddGodModeAuth();
        builder.Services.AddAuthorization();
        break;
}

// Data Protection (used for OAuth token encryption)
var projectRootsDir = Path.GetFullPath(builder.Configuration["ProjectRootsDir"] ?? "roots");
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(projectRootsDir, ".godmode-keys")));

// OAuth proxy services
builder.Services.AddSingleton<OAuthCsrfStore>();
builder.Services.AddSingleton<OAuthProxyClient>();
builder.Services.AddSingleton<OAuthTokenStore>();

// Register application services
builder.Services.AddSingleton<IClaudeProcessManager, ClaudeProcessManager>();
builder.Services.AddSingleton<IStatusUpdater, StatusUpdater>();
builder.Services.AddSingleton<IRootConfigReader, RootConfigReader>();
builder.Services.AddSingleton<IScriptRunner, ScriptRunner>();
builder.Services.AddSingleton<ProfileFileManager>();
builder.Services.AddSingleton<RootCreator>();
builder.Services.AddSingleton<RootPackager>();
builder.Services.AddSingleton<RootInstaller>();
builder.Services.AddSingleton<IManifestParser, ManifestParser>();
builder.Services.AddSingleton<IConvergenceEngine, ConvergenceEngine>();
builder.Services.AddSingleton<IManifestExporter, ManifestExporter>();
builder.Services.AddGodModeAIServices();
builder.Services.AddSingleton<RootGenerationService>();
builder.Services.AddSingleton<GodModeChatService>();
builder.Services.AddSingleton<WebhookFileManager>();
builder.Services.AddSingleton<IProjectManager, ProjectManager>();
builder.Services.AddSingleton<ScheduleManager>();
builder.Services.AddSingleton<McpOAuthStore>();
builder.Services.Configure<BackupConfig>(builder.Configuration.GetSection("Backup"));
builder.Services.AddSingleton<BackupService>();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
    app.UseCors();

if (authMode != "none")
{
    app.UseAuthentication();
    app.UseAuthorization();
}

// Serve the React client from wwwroot/ (if present)
app.UseDefaultFiles();
app.UseStaticFiles();

// ── Auth endpoints (/api/auth/*) ───────────────────────────────

var auth = app.MapGroup("/api/auth");

// Challenge: tells the React client what auth method is required
auth.MapGet("/challenge", (HttpContext ctx) =>
{
    return Results.Ok(new
    {
        method = authMode,
        authenticated = ctx.User.Identity?.IsAuthenticated == true || authMode == "none",
    });
}).AllowAnonymous();

// Logout (shared across auth methods)
auth.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync();
    return Results.Ok(new { success = true });
}).AllowAnonymous();

// ── OAuth proxy endpoints (/api/oauth/*) ─────────────────────────

var oauth = app.MapGroup("/api/oauth");

// Initiate: redirect browser to OAuth proxy
oauth.MapGet("/initiate", (
    HttpContext ctx,
    OAuthCsrfStore csrfStore,
    OAuthProxyClient proxyClient,
    string provider,
    string? profileId,
    string? purpose) =>
{
    purpose ??= "connector";
    if (!OAuthProviderMapping.IsSupported(provider) && provider != "google")
        return Results.BadRequest(new { error = $"Unsupported provider: {provider}" });

    var csrf = csrfStore.Generate(provider, profileId, purpose);

    // Determine this server's public URL
    var instanceUrl = ctx.Request.Headers["X-Forwarded-Proto"].FirstOrDefault() is { } proto
        ? $"{proto}://{ctx.Request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? ctx.Request.Host.ToString()}"
        : $"{ctx.Request.Scheme}://{ctx.Request.Host}";

    // Login needs email scope; connector flows use provider defaults
    var scope = purpose == "login" && provider == "google" ? "openid email profile" : null;
    var authorizeUrl = proxyClient.BuildAuthorizeUrl(provider, instanceUrl, csrf, profileId, scope);
    return Results.Redirect(authorizeUrl);
}).AllowAnonymous();

// Relay: callback from OAuth proxy after successful auth
oauth.MapGet("/relay", async (
    HttpContext ctx,
    OAuthCsrfStore csrfStore,
    OAuthProxyClient proxyClient,
    OAuthTokenStore tokenStore,
    IHubContext<ProjectHub, IProjectHubClient> hubContext,
    ILogger<Program> logger,
    string relay,
    string provider,
    string state,
    string? profile) =>
{
    // Validate CSRF
    var csrfEntry = csrfStore.Validate(state);
    if (csrfEntry == null)
    {
        logger.LogWarning("OAuth relay: invalid CSRF state");
        return Results.Redirect("/?error=csrf_mismatch");
    }

    // Redeem relay token
    var tokens = await proxyClient.RedeemRelayTokenAsync(relay);
    if (tokens == null)
    {
        logger.LogWarning("OAuth relay: failed to redeem relay token");
        return Results.Redirect("/?error=relay_failed");
    }

    // Login flow
    if (csrfEntry.Purpose == "login")
    {
        var googleOptions = ctx.RequestServices.GetService<GoogleAuthOptions>();
        // Only trust the proxy's Email claim if the proxy has explicitly asserted EmailVerified=true.
        // Otherwise fetch from Google directly, which enforces the verified_email/email_verified flag.
        // Using an unverified email for login would allow a federated Workspace tenant (or other
        // source of unverified claims) to impersonate the AllowedEmail.
        var proxyEmailVerified = tokens.EmailVerified == true;
        var email = proxyEmailVerified ? tokens.Email?.Trim().ToLowerInvariant() : null;
        var name = proxyEmailVerified ? tokens.Name : null;

        if (string.IsNullOrEmpty(email) && provider == "google")
        {
            var (fetchedEmail, fetchedName) = await proxyClient.FetchGoogleUserInfoAsync(tokens.AccessToken);
            email = fetchedEmail?.Trim().ToLowerInvariant();
            name ??= fetchedName;
        }

        if (googleOptions == null || string.IsNullOrEmpty(email) || email != googleOptions.AllowedEmail)
        {
            logger.LogWarning("OAuth login: access denied for {Email}", email);
            return Results.Redirect("/?error=access_denied");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Name, name ?? email),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        logger.LogInformation("OAuth login: {Email} authenticated via proxy", email);
        return Results.Redirect("/");
    }

    // Connector flow: store tokens in profile
    var profileName = profile ?? csrfEntry.ProfileId ?? "default";
    tokenStore.StoreTokens(profileName, provider, tokens);
    await hubContext.Clients.All.OAuthStatusChanged(profileName);

    logger.LogInformation("OAuth connector: stored {Provider} tokens for profile {Profile}", provider, profileName);
    return Results.Redirect($"/?oauthSuccess={Uri.EscapeDataString(provider)}");
}).AllowAnonymous();

// ── MCP OAuth endpoints (remote MCP servers with OAuth, e.g. Google Workspace) ──

var mcpOAuth = app.MapGroup("/api/mcp-oauth");

// MCP OAuth pending flows are stored in OAuthCsrfStore (has timer-based cleanup)

// Initiate: discover endpoints, register with remote MCP server, then redirect to its /authorize
mcpOAuth.MapGet("/initiate", async (
    HttpContext ctx,
    OAuthCsrfStore csrfStore,
    McpOAuthStore mcpStore,
    IHttpClientFactory httpFactory,
    ILogger<Program> logger,
    string connectorId,
    string profileName,
    string mcpServerUrl) =>
{
    // SSRF protection: only allow HTTPS URLs to known MCP server hosts
    if (!Uri.TryCreate(mcpServerUrl, UriKind.Absolute, out var mcpUri) ||
        mcpUri.Scheme != "https" ||
        System.Net.IPAddress.TryParse(mcpUri.Host, out _))
    {
        return Results.BadRequest(new { error = "Invalid MCP server URL. Must be HTTPS with a hostname." });
    }

    var baseUrl = $"{mcpUri.Scheme}://{mcpUri.Host}";
    var http = httpFactory.CreateClient();

    // Determine this server's public URL for the redirect
    var instanceUrl = ctx.Request.Headers["X-Forwarded-Proto"].FirstOrDefault() is { } proto
        ? $"{proto}://{ctx.Request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? ctx.Request.Host.ToString()}"
        : $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    var redirectUri = $"{instanceUrl}/api/mcp-oauth/callback";

    // Step 0: Discover OAuth endpoints via .well-known metadata
    string registrationEndpoint, authorizeEndpoint, tokenEndpoint;
    try
    {
        var metaResp = await http.GetAsync($"{baseUrl}/.well-known/oauth-authorization-server");
        var meta = await metaResp.Content.ReadFromJsonAsync<JsonElement>();
        registrationEndpoint = meta.GetProperty("registration_endpoint").GetString()!;
        authorizeEndpoint = meta.GetProperty("authorization_endpoint").GetString()!;
        tokenEndpoint = meta.GetProperty("token_endpoint").GetString()!;
        // Validate discovered endpoints are on the same host or trusted hosts
        var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mcpUri.Host };
        foreach (var ep in new[] { registrationEndpoint, authorizeEndpoint, tokenEndpoint })
        {
            if (Uri.TryCreate(ep, UriKind.Absolute, out var epUri) &&
                !allowedHosts.Contains(epUri.Host))
            {
                // Allow well-known auth providers (Atlassian uses cf.mcp.atlassian.com)
                if (!epUri.Host.EndsWith($".{mcpUri.Host}", StringComparison.OrdinalIgnoreCase) &&
                    !epUri.Host.EndsWith(".atlassian.com", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("MCP OAuth: discovered endpoint {Ep} has untrusted host, rejecting", ep);
                    return Results.BadRequest(new { error = "Discovered OAuth endpoint has untrusted host." });
                }
            }
        }
        logger.LogInformation("MCP OAuth: discovered endpoints — register: {Reg}, authorize: {Auth}, token: {Token}",
            registrationEndpoint, authorizeEndpoint, tokenEndpoint);
    }
    catch (Exception ex)
    {
        // Fallback: assume standard paths relative to base URL
        logger.LogWarning(ex, "MCP OAuth: .well-known discovery failed, using defaults for {BaseUrl}", baseUrl);
        registrationEndpoint = $"{baseUrl}/register";
        authorizeEndpoint = $"{baseUrl}/authorize";
        tokenEndpoint = $"{baseUrl}/token";
    }

    // Step 1: Dynamic client registration
    string clientId;
    try
    {
        var regBody = JsonSerializer.Serialize(new
        {
            client_name = "GodMode",
            redirect_uris = new[] { redirectUri },
            token_endpoint_auth_method = "none",
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
        });
        var regResp = await http.PostAsync(registrationEndpoint,
            new StringContent(regBody, System.Text.Encoding.UTF8, "application/json"));
        if (!regResp.IsSuccessStatusCode)
        {
            var err = await regResp.Content.ReadAsStringAsync();
            logger.LogError("MCP OAuth: registration failed ({Status}): {Error}", regResp.StatusCode, err);
            return Results.Redirect($"/?error=mcp_register_failed");
        }
        var regJson = await regResp.Content.ReadFromJsonAsync<JsonElement>();
        clientId = regJson.GetProperty("client_id").GetString()!;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "MCP OAuth: failed to register client with {Endpoint}", registrationEndpoint);
        return Results.Redirect($"/?error=mcp_register_failed");
    }

    // Step 2: PKCE
    var verifierBytes = new byte[32];
    System.Security.Cryptography.RandomNumberGenerator.Fill(verifierBytes);
    var codeVerifier = Convert.ToBase64String(verifierBytes)
        .Replace("+", "-").Replace("/", "_").TrimEnd('=');
    var challengeBytes = System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.ASCII.GetBytes(codeVerifier));
    var codeChallenge = Convert.ToBase64String(challengeBytes)
        .Replace("+", "-").Replace("/", "_").TrimEnd('=');

    // Step 3: State token
    var state = Guid.NewGuid().ToString("N");
    csrfStore.StoreMcpPending(state, new OAuthCsrfStore.McpOAuthPendingFlow(
        connectorId, profileName, codeVerifier, mcpServerUrl, clientId, redirectUri, tokenEndpoint, DateTime.UtcNow));

    // Step 4: Redirect to MCP server's authorize endpoint
    var authorizeUrl = $"{authorizeEndpoint}?" + string.Join("&",
        $"response_type=code",
        $"client_id={Uri.EscapeDataString(clientId)}",
        $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
        $"state={Uri.EscapeDataString(state)}",
        $"code_challenge={Uri.EscapeDataString(codeChallenge)}",
        $"code_challenge_method=S256",
        $"scope=");

    logger.LogInformation("MCP OAuth: redirecting to {AuthorizeUrl}", authorizeUrl);
    return Results.Redirect(authorizeUrl);
}).AllowAnonymous();

// Callback: exchange code for token
mcpOAuth.MapGet("/callback", async (
    HttpContext ctx,
    OAuthCsrfStore csrfStore,
    McpOAuthStore mcpStore,
    IHttpClientFactory httpFactory,
    ILogger<Program> logger,
    string code,
    string state) =>
{
    var pending = csrfStore.ConsumeMcpPending(state);
    if (pending == null)
    {
        logger.LogWarning("MCP OAuth: invalid state");
        return Results.Redirect("/?error=mcp_oauth_state_invalid");
    }

    // Exchange code for token using discovered token endpoint
    var http = httpFactory.CreateClient();
    try
    {
        var tokenBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = pending.ClientId,
            ["redirect_uri"] = pending.RedirectUri,
            ["code_verifier"] = pending.CodeVerifier,
        });
        var tokenResp = await http.PostAsync(pending.TokenEndpoint, tokenBody);
        if (!tokenResp.IsSuccessStatusCode)
        {
            var err = await tokenResp.Content.ReadAsStringAsync();
            logger.LogError("MCP OAuth: token exchange failed: {Error}", err);
            return Results.Redirect("/?error=mcp_oauth_token_failed");
        }

        var tokenJson = await tokenResp.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = tokenJson.GetProperty("access_token").GetString()!;
        var refreshToken = tokenJson.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var expiresIn = tokenJson.TryGetProperty("expires_in", out var ei) ? ei.GetInt64() : 3600;

        mcpStore.Store(pending.ProfileName, pending.ConnectorId, new McpOAuthTokens(
            accessToken, refreshToken, pending.ClientId, pending.McpServerUrl,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn, null));

        logger.LogInformation("MCP OAuth: stored tokens for {Profile}/{Connector}",
            pending.ProfileName, pending.ConnectorId);

        return Results.Redirect($"/?mcpOAuthSuccess={Uri.EscapeDataString(pending.ConnectorId)}");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "MCP OAuth: token exchange failed");
        return Results.Redirect("/?error=mcp_oauth_error");
    }
}).AllowAnonymous();

// Status: check if a connector has MCP OAuth tokens
mcpOAuth.MapGet("/status", (string profileName, McpOAuthStore mcpStore) =>
{
    if (profileName.Contains("..") || profileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        return Results.BadRequest(new { error = "Invalid profile name" });
    var tokens = mcpStore.GetAllForProfile(profileName);
    return Results.Ok(tokens.ToDictionary(
        kvp => kvp.Key,
        kvp => new { Connected = true, kvp.Value.Email }));
});

// Disconnect: delete MCP OAuth tokens for a connector
mcpOAuth.MapPost("/disconnect", (string profileName, string connectorId, McpOAuthStore mcpStore) =>
{
    if (profileName.Contains("..") || profileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
        connectorId.Contains("..") || connectorId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        return Results.BadRequest(new { error = "Invalid profile or connector name" });
    mcpStore.Delete(profileName, connectorId);
    return Results.Ok(new { success = true });
});

app.MapGet("/api/status", () => new
{
    service = "GodMode.Server",
    version = "1.0.0",
    status = "running"
}).AllowAnonymous();

// ── Backup / restore endpoints (/api/backup/*) ───────────────────
// Snapshots ProjectRootsDir (profiles, webhooks, data-protection keys, every
// project's chat history) into a tar.gz on Backup:Location, and restores
// from one. The location is intended to be a mounted shared/remote path.

var backup = app.MapGroup("/api/backup");

backup.MapPost("/", async (BackupService svc, ILogger<Program> log, CancellationToken ct) =>
{
    try
    {
        var result = await svc.CreateBackupAsync(ct);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        log.LogWarning(ex, "Backup precondition failed");
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Backup failed");
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

backup.MapGet("/", (BackupService svc) =>
{
    try
    {
        return Results.Ok(new { location = svc.Location, items = svc.ListBackups() });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

backup.MapPost("/restore", async (BackupRestoreRequest? req, BackupService svc, ILogger<Program> log, CancellationToken ct) =>
{
    try
    {
        var result = await svc.RestoreBackupAsync(req?.FileName, ct);
        return Results.Ok(result);
    }
    catch (FileNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        log.LogWarning(ex, "Restore precondition failed");
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Restore failed");
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

app.MapGet("/health", () => new { status = "healthy" }).AllowAnonymous();

// ── React client API surface (matches MAUI LocalServer) ────────

// Server list: return this server as the only entry
app.MapGet("/servers", () => new[]
{
    new ServerInfo("self", "Local Server", "local", ServerState.Running)
}).AllowAnonymous();

// SSE event stream (placeholder — no dynamic server changes in single-server mode)
app.MapGet("/events", async (HttpContext ctx) =>
{
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";
    await ctx.Response.Body.FlushAsync();
    // Keep connection open until client disconnects
    try { await Task.Delay(Timeout.Infinite, ctx.RequestAborted); }
    catch (OperationCanceledException) { }
}).AllowAnonymous();

var hub = app.MapHub<ProjectHub>("/hubs/projects");
if (authMode != "none")
    hub.RequireAuthorization();

// ── Webhook endpoint (uses per-webhook token auth, not server auth) ──

app.MapPost("/webhook/{keyword}", async (string keyword, HttpContext ctx,
    WebhookFileManager webhookManager, IProjectManager pm,
    IHubContext<ProjectHub, IProjectHubClient> hubContext, ILogger<Program> logger) =>
{
    // Read webhook config
    var config = webhookManager.Read(keyword);
    if (config == null)
        return Results.NotFound(new { error = $"Webhook '{keyword}' not found." });

    // Validate bearer token
    var authHeader = ctx.Request.Headers.Authorization.ToString();
    if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        return Results.Json(new { error = "Missing webhook token. Use: Authorization: Bearer <token>" }, statusCode: 401);

    var token = authHeader["Bearer ".Length..].Trim();
    if (!webhookManager.ValidateToken(keyword, token))
        return Results.Json(new { error = "Invalid webhook token." }, statusCode: 401);

    if (!config.Enabled)
        return Results.Json(new { error = $"Webhook '{keyword}' is disabled." }, statusCode: 403);

    // Parse payload
    JsonElement? payload = null;
    if (ctx.Request.ContentLength > 0 || ctx.Request.ContentType?.Contains("json") == true)
    {
        try
        {
            payload = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Invalid JSON payload." });
        }
    }

    // Map payload to inputs
    var inputs = WebhookPayloadMapper.MapPayload(config, payload);

    // Create project
    try
    {
        var request = new CreateProjectRequest(config.ProfileName, config.RootName, inputs, config.ActionName);
        var status = await pm.CreateProjectAsync(request);
        await hubContext.Clients.All.ProjectCreated(status);

        logger.LogInformation("Webhook '{Keyword}' triggered project '{ProjectName}' ({ProjectId})",
            keyword, status.Name, status.Id);

        return Results.Json(new WebhookResult(status.Id, status.Name, status.State.ToString()),
            statusCode: 202);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Webhook '{Keyword}' failed to create project", keyword);
        return Results.Json(new { error = $"Project creation failed: {ex.Message}" }, statusCode: 500);
    }
}).AllowAnonymous();

// ── Internal API (MCP bridge → server, project-scoped token auth) ──

var internalApi = app.MapGroup("/api/internal");

// Helper: extract and validate project token from request
static (string projectId, string token)? ParseInternalAuth(HttpContext ctx)
{
    var authHeader = ctx.Request.Headers.Authorization.ToString();
    if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        return null;
    var token = authHeader["Bearer ".Length..].Trim();
    var projectId = ctx.Request.Headers["X-GodMode-Project-Id"].ToString();
    if (string.IsNullOrEmpty(projectId))
        return null;
    return (projectId, token);
}

internalApi.MapPost("/result", async (HttpContext ctx, IProjectManager pm) =>
{
    var auth = ParseInternalAuth(ctx);
    if (auth == null || pm.ValidateProjectToken(auth.Value.projectId, auth.Value.token) == null)
        return Results.Json(new { error = "Invalid project token" }, statusCode: 401);

    var request = await ctx.Request.ReadFromJsonAsync<SubmitResultRequest>();
    if (request == null)
        return Results.BadRequest(new { error = "Invalid request body" });

    await pm.StoreProjectResultAsync(auth.Value.projectId, request);
    return Results.Ok(new { success = true });
}).AllowAnonymous();

internalApi.MapPost("/status", async (HttpContext ctx, IProjectManager pm) =>
{
    var auth = ParseInternalAuth(ctx);
    if (auth == null || pm.ValidateProjectToken(auth.Value.projectId, auth.Value.token) == null)
        return Results.Json(new { error = "Invalid project token" }, statusCode: 401);

    var request = await ctx.Request.ReadFromJsonAsync<UpdateStatusRequest>();
    if (request == null)
        return Results.BadRequest(new { error = "Invalid request body" });

    await pm.UpdateCustomStatusAsync(auth.Value.projectId, request.Message);
    return Results.Ok(new { success = true });
}).AllowAnonymous();

internalApi.MapPost("/review", async (HttpContext ctx, IProjectManager pm) =>
{
    var auth = ParseInternalAuth(ctx);
    if (auth == null || pm.ValidateProjectToken(auth.Value.projectId, auth.Value.token) == null)
        return Results.Json(new { error = "Invalid project token" }, statusCode: 401);

    var request = await ctx.Request.ReadFromJsonAsync<RequestReviewRequest>();
    if (request == null)
        return Results.BadRequest(new { error = "Invalid request body" });

    await pm.RequestHumanReviewAsync(auth.Value.projectId, request);
    return Results.Ok(new { success = true });
}).AllowAnonymous();

// SPA fallback: serve index.html for non-API/non-hub routes (React client routing)
app.MapFallbackToFile("index.html");

app.Logger.LogInformation("Authentication mode: {AuthMode}", authMode);

// Apply manifest on startup if configured
var manifestPath = builder.Configuration["Manifest"];
if (!string.IsNullOrEmpty(manifestPath))
{
    var parser = app.Services.GetRequiredService<IManifestParser>();
    var engine = app.Services.GetRequiredService<IConvergenceEngine>();
    try
    {
        var manifest = parser.ParseFile(manifestPath);
        var result = engine.ConvergeAsync(manifest).GetAwaiter().GetResult();
        app.Logger.LogInformation("Startup convergence: {Actions} actions, {Errors} errors",
            result.Actions.Count, result.Errors.Count);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Failed to apply manifest from {ManifestPath}", manifestPath);
    }
}

// Initialize inference router (loads AI providers)
var inferenceRouter = app.Services.GetRequiredService<InferenceRouter>();
try
{
    await inferenceRouter.InitializeAsync();
    app.Logger.LogInformation("Inference router initialized: {Status}", inferenceRouter.IsLoaded ? "ready" : "no providers");
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Failed to initialize inference router");
}

// Initialize schedule timers
var scheduleManager = app.Services.GetRequiredService<ScheduleManager>();
scheduleManager.Initialize();

// Recover existing projects AFTER server starts (non-blocking)
var projectManager = app.Services.GetRequiredService<IProjectManager>();
app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(async () =>
    {
        try
        {
            await projectManager.RecoverProjectsAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error recovering projects: {ex.Message}");
        }
    });
});

app.Run();

