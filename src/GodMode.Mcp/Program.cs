using Amazon.DynamoDBv2;
using GodMode.Mcp;
using GodMode.Mcp.Auth;
using GodMode.Mcp.OAuth;
using GodMode.Mcp.Resources;
using GodMode.Mcp.Tools;
using ModelContextProtocol.Protocol;

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

// Agent output and session tracking (singleton so we can capture for handler)
var agentOutputStore = new AgentOutputStore();
builder.Services.AddSingleton(agentOutputStore);
builder.Services.AddSingleton<McpSessionTracker>();

// MCP Server
builder.Services.AddMcpServer()
    .WithHttpTransport(options =>
    {
        options.RunSessionHandler = async (httpContext, mcpServer, token) =>
        {
            var tracker = httpContext.RequestServices.GetRequiredService<McpSessionTracker>();
            var sessionId = mcpServer.SessionId ?? Guid.NewGuid().ToString();

            tracker.Register(sessionId, mcpServer);
            try
            {
                await mcpServer.RunAsync(token);
            }
            finally
            {
                tracker.Unregister(sessionId);
            }
        };
    })
    .WithListResourcesHandler((request, ct) =>
    {
        var agents = agentOutputStore.GetAllAgentNames();

        var resources = new List<Resource>
        {
            // Static config resource
            new()
            {
                Uri = "config://default",
                Name = "Default Configuration",
                Description = "Returns the default configuration for this MCP server"
            }
        };

        // Add dynamic agent resources
        foreach (var agent in agents)
        {
            resources.Add(new Resource
            {
                Uri = $"agent://{agent}/output",
                Name = $"Agent: {agent}",
                Description = $"Real-time output from Claude Code agent '{agent}'"
            });
        }

        return ValueTask.FromResult(new ListResourcesResult { Resources = resources });
    })
    .WithTools<CodespacesTools>()
    .WithResources<ConfigResources>()
    .WithResources<AgentResources>();

var app = builder.Build();

// OAuth endpoints (no auth required)
app.MapOAuthEndpoints();

// Agent output trigger endpoint (for testing resource notifications)
// POST /agent/{name}/output with body: { "line": "some output text" }
app.MapPost("/agent/{name}/output", async (
    string name,
    AgentOutputRequest request,
    AgentOutputStore store,
    McpSessionTracker tracker) =>
{
    var isNewAgent = store.AppendOutput(name, request.Line);

    // If this is a new agent, notify that the resource list changed
    if (isNewAgent)
    {
        await tracker.NotifyResourceListChangedAsync();
    }

    // Always notify that this specific resource was updated
    await tracker.NotifyResourceUpdatedAsync($"agent://{name}/output");
    return Results.Ok(new { success = true, agent = name, line = request.Line, isNewAgent });
});

// MCP endpoint (auth required)
app.UseAuthentication();
app.UseAuthorization();
app.UseGitHubAuthExceptionHandler();

app.MapMcp("/mcp")
    .RequireAuthorization();

app.Run();

record AgentOutputRequest(string Line);
