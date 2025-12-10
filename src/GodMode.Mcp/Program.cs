using Amazon.DynamoDBv2;
using GodMode.Mcp.Auth;
using GodMode.Mcp.OAuth;
using GodMode.Mcp.Tools;
using Octokit;

var builder = WebApplication.CreateBuilder(args);

// Add AWS Lambda support
builder.Services.AddAWSLambdaHosting(LambdaEventSource.RestApi);

// DynamoDB
builder.Services.AddSingleton<IAmazonDynamoDB, AmazonDynamoDBClient>();
builder.Services.AddSingleton<IOAuthStore, DynamoDbOAuthStore>();

// GitHub OAuth service
builder.Services.AddHttpClient<GitHubOAuthService>();

// Authentication
builder.Services.AddAuthentication(GodModeMcpAuthExtensions.SchemeName)
    .AddGodModeMcpAuth();
builder.Services.AddAuthorization();

// GitHub client - scoped per request, configured from auth context
builder.Services.AddScoped<IGitHubClient>(sp =>
{
    var httpContextAccessor = sp.GetRequiredService<IHttpContextAccessor>();
    var user = httpContextAccessor.HttpContext?.User;

    var gitHubToken = user?.FindFirst("github_access_token")?.Value;

    if (string.IsNullOrEmpty(gitHubToken))
    {
        // Return unauthenticated client for non-authenticated requests
        return new GitHubClient(new ProductHeaderValue("GodMode-MCP"));
    }

    var client = new GitHubClient(new ProductHeaderValue("GodMode-MCP"))
    {
        Credentials = new Credentials(gitHubToken)
    };
    return client;
});

builder.Services.AddHttpContextAccessor();

// MCP Server
builder.Services.AddMcpServer()
    .WithTools<GitHubTools>();

var app = builder.Build();

// OAuth endpoints (no auth required)
app.MapOAuthEndpoints();

// MCP endpoint (auth required)
app.UseAuthentication();
app.UseAuthorization();

app.MapMcp("/mcp")
    .RequireAuthorization();

app.Run();
