using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using TypedSignalR.Client;

/// <summary>
/// Test client for GodMode SignalR server.
/// Creates a project and observes output.
/// </summary>

var serverUrl = "http://localhost:31337/hubs/projects";

Console.WriteLine("=== SIGNALR TEST CLIENT ===");
Console.WriteLine($"Connecting to: {serverUrl}");
Console.WriteLine();

// Build connection
var connection = new HubConnectionBuilder()
    .WithUrl(serverUrl)
    .WithAutomaticReconnect()
    .Build();

// Create typed hub proxy
var hub = connection.CreateHubProxy<IProjectHub>();

// Register client handlers
var clientHandler = new ProjectHubClientHandler();
var registration = connection.Register<IProjectHubClient>(clientHandler);

// Connect
await connection.StartAsync();
Console.WriteLine($"Connected! State: {connection.State}");
Console.WriteLine();

// List project roots
Console.WriteLine("=== PROJECT ROOTS ===");
var roots = await hub.ListProjectRoots();
foreach (var root in roots)
{
    Console.WriteLine($"  - {root.Name}: {root.Path}");
}
Console.WriteLine();

// List existing projects
Console.WriteLine("=== EXISTING PROJECTS ===");
var projects = await hub.ListProjects();
if (projects.Length == 0)
{
    Console.WriteLine("  (none)");
}
else
{
    foreach (var p in projects)
    {
        Console.WriteLine($"  - {p.Id}: {p.Name} ({p.State})");
    }
}
Console.WriteLine();

// Create a new project
var projectName = $"test_{DateTime.Now:HHmmss}";
var initialPrompt = "Just say hi back. Keep it very short.";

Console.WriteLine("=== CREATING PROJECT ===");
Console.WriteLine($"Name: {projectName}");
Console.WriteLine($"Prompt: {initialPrompt}");
Console.WriteLine();

var detail = await hub.CreateProject(
    name: projectName,
    projectRootName: "default",
    projectType: ProjectType.RawFolder,
    repoUrl: null,
    initialPrompt: initialPrompt
);

Console.WriteLine($"Created project: {detail.Status.Id}");
Console.WriteLine($"Session ID: {detail.SessionId}");
Console.WriteLine($"State: {detail.Status.State}");
Console.WriteLine();

// Subscribe to output
Console.WriteLine("=== SUBSCRIBING TO OUTPUT ===");
await hub.SubscribeProject(detail.Status.Id, 0);
Console.WriteLine("Subscribed. Waiting for output events...");
Console.WriteLine();

// Wait for output (timeout after 30 seconds)
var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
try
{
    while (!cts.Token.IsCancellationRequested)
    {
        await Task.Delay(500, cts.Token);

        // Check if we got a result
        if (clientHandler.ReceivedResult)
        {
            Console.WriteLine();
            Console.WriteLine("Got result event - stopping.");
            break;
        }
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine();
    Console.WriteLine("Timeout waiting for events.");
}

// Get final status
Console.WriteLine();
Console.WriteLine("=== FINAL STATUS ===");
var status = await hub.GetStatus(detail.Status.Id);
Console.WriteLine($"State: {status.State}");
Console.WriteLine($"Current Question: {status.CurrentQuestion ?? "(none)"}");
Console.WriteLine();

// Cleanup
await hub.UnsubscribeProject(detail.Status.Id);
registration.Dispose();
await connection.StopAsync();

Console.WriteLine("Done.");

// Client handler implementation
class ProjectHubClientHandler : IProjectHubClient
{
    public bool ReceivedResult { get; private set; }

    public Task OutputReceived(string projectId, OutputEvent outputEvent)
    {
        Console.WriteLine($"[OUTPUT] Project: {projectId}");
        Console.WriteLine($"         Type: {outputEvent.Type}");

        if (!string.IsNullOrEmpty(outputEvent.Content))
        {
            var content = outputEvent.Content.Length > 200
                ? outputEvent.Content[..200] + "..."
                : outputEvent.Content;
            Console.WriteLine($"         Content: {content}");
        }

        // Check for result event which indicates completion
        if (outputEvent.Type == OutputEventType.Result)
        {
            ReceivedResult = true;
        }

        return Task.CompletedTask;
    }

    public Task StatusChanged(string projectId, ProjectStatus status)
    {
        Console.WriteLine($"[STATUS] Project: {projectId} -> {status.State}");
        return Task.CompletedTask;
    }

    public Task ProjectCreated(ProjectStatus status)
    {
        Console.WriteLine($"[CREATED] Project: {status.Id} ({status.Name})");
        return Task.CompletedTask;
    }
}
