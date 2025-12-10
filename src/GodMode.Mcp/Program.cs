using Amazon.DynamoDBv2;
using GodMode.Mcp.Auth;
using GodMode.Mcp.OAuth;
using GodMode.Mcp.Resources;
using GodMode.Mcp.Tools;

var builder = WebApplication.CreateBuilder(args);

// Storage: Use file-based for Development, DynamoDB for production
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<IOAuthStore, FileOAuthStore>();
}
else
{
    builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);
    builder.Services.AddSingleton<IAmazonDynamoDB, AmazonDynamoDBClient>();
    builder.Services.AddSingleton<IOAuthStore, DynamoDbOAuthStore>();
}

// GitHub OAuth service
builder.Services.AddHttpClient<GitHubOAuthService>();
builder.Services.AddHttpClient<CodespacesTools>();

// Authentication
builder.Services.AddAuthentication(GodModeMcpAuthExtensions.SchemeName)
    .AddGodModeMcpAuth();
builder.Services.AddAuthorization();

builder.Services.AddHttpContextAccessor();

// MCP Server
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<CodespacesTools>()
    .WithResources<ConfigResources>();

var app = builder.Build();

// OAuth endpoints (no auth required)
app.MapOAuthEndpoints();

// MCP endpoint (auth required)
app.UseAuthentication();
app.UseAuthorization();
app.UseGitHubAuthExceptionHandler();

app.MapMcp("/mcp")
    .RequireAuthorization();

app.Run();
