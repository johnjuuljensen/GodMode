using GodMode.Shared.Enums;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using TypedSignalR.Client;

/// <summary>
/// Test client for GodMode SignalR server.
/// Tests full project lifecycle including reconnection and history.
/// </summary>

var serverUrl = "http://localhost:31337/hubs/projects";

Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║         SIGNALR FULL LIFECYCLE TEST CLIENT                   ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
Console.WriteLine($"Server: {serverUrl}");
Console.WriteLine();

// ═══════════════════════════════════════════════════════════════════
// PHASE 1: Initial Connection and Project Creation
// ═══════════════════════════════════════════════════════════════════
Console.WriteLine("┌────────────────────────────────────────────────────────────────┐");
Console.WriteLine("│ PHASE 1: Initial Connection and Project Creation              │");
Console.WriteLine("└────────────────────────────────────────────────────────────────┘");

var (connection1, hub1, clientHandler1, registration1) = await CreateConnection(serverUrl);

// List project roots
Console.WriteLine("\n[INFO] Available project roots:");
var roots = await hub1.ListProjectRoots();
foreach (var root in roots)
{
    Console.WriteLine($"  - {root.Name}: {root.Path}");
}

// Create a new project
var projectName = $"lifecycle_test_{DateTime.Now:HHmmss}";
var initialPrompt = "Just say 'Hello from Claude!' and nothing else.";

Console.WriteLine($"\n[ACTION] Creating project: {projectName}");
Console.WriteLine($"[ACTION] Initial prompt: {initialPrompt}");

var detail = await hub1.CreateProject(
    name: projectName,
    projectRootName: "default",
    projectType: ProjectType.RawFolder,
    repoUrl: null,
    initialPrompt: initialPrompt
);

var projectId = detail.Status.Id;
Console.WriteLine($"[SUCCESS] Created project: {projectId}");
Console.WriteLine($"[INFO] Session ID: {detail.SessionId}");

// ═══════════════════════════════════════════════════════════════════
// PHASE 2: Subscribe and Interact
// ═══════════════════════════════════════════════════════════════════
Console.WriteLine("\n┌────────────────────────────────────────────────────────────────┐");
Console.WriteLine("│ PHASE 2: Subscribe and Interact                               │");
Console.WriteLine("└────────────────────────────────────────────────────────────────┘");

Console.WriteLine("\n[ACTION] Subscribing to project output (offset 0)...");
await hub1.SubscribeProject(projectId, 0);
Console.WriteLine("[SUCCESS] Subscribed");

Console.WriteLine("\n[WAITING] Waiting for Claude's initial response...");
await WaitForResult(clientHandler1, TimeSpan.FromSeconds(60));

var phase2EventCount = clientHandler1.EventCount;
Console.WriteLine($"\n[INFO] Received {phase2EventCount} events in Phase 2");
Console.WriteLine("[INFO] Events received:");
foreach (var msg in clientHandler1.ReceivedMessages)
{
    var textProp = msg.Properties.FirstOrDefault(p => p.Name == "text" || p.Name == "result" || p.Name == "subtype");
    var contentPreview = textProp?.Value ?? "(structured)";
    if (contentPreview.Length > 60) contentPreview = contentPreview[..60] + "...";
    Console.WriteLine($"  [{msg.Type}] {contentPreview}");
}

// ═══════════════════════════════════════════════════════════════════
// PHASE 3: Stop Project
// ═══════════════════════════════════════════════════════════════════
Console.WriteLine("\n┌────────────────────────────────────────────────────────────────┐");
Console.WriteLine("│ PHASE 3: Stop Project                                         │");
Console.WriteLine("└────────────────────────────────────────────────────────────────┘");

Console.WriteLine("\n[ACTION] Stopping project...");
await hub1.StopProject(projectId);
Console.WriteLine("[SUCCESS] Project stopped");

var status = await hub1.GetStatus(projectId);
Console.WriteLine($"[INFO] Project state: {status.State}");

// ═══════════════════════════════════════════════════════════════════
// PHASE 4: Disconnect from Server
// ═══════════════════════════════════════════════════════════════════
Console.WriteLine("\n┌────────────────────────────────────────────────────────────────┐");
Console.WriteLine("│ PHASE 4: Disconnect from Server                               │");
Console.WriteLine("└────────────────────────────────────────────────────────────────┘");

Console.WriteLine("\n[ACTION] Unsubscribing from project...");
await hub1.UnsubscribeProject(projectId);
Console.WriteLine("[SUCCESS] Unsubscribed");

Console.WriteLine("[ACTION] Disconnecting from server...");
registration1.Dispose();
await connection1.StopAsync();
Console.WriteLine("[SUCCESS] Disconnected");

Console.WriteLine("\n[INFO] Simulating disconnection period (2 seconds)...");
await Task.Delay(2000);

// ═══════════════════════════════════════════════════════════════════
// PHASE 5: Reconnect to Server
// ═══════════════════════════════════════════════════════════════════
Console.WriteLine("\n┌────────────────────────────────────────────────────────────────┐");
Console.WriteLine("│ PHASE 5: Reconnect to Server (NEW connection)                 │");
Console.WriteLine("└────────────────────────────────────────────────────────────────┘");

var (connection2, hub2, clientHandler2, registration2) = await CreateConnection(serverUrl);

// List projects to find our project
Console.WriteLine("\n[ACTION] Listing existing projects...");
var projects = await hub2.ListProjects();
Console.WriteLine($"[INFO] Found {projects.Length} project(s):");
foreach (var p in projects)
{
    var marker = p.Id == projectId ? " <-- OUR PROJECT" : "";
    Console.WriteLine($"  - {p.Id}: {p.Name} ({p.State}){marker}");
}

// ═══════════════════════════════════════════════════════════════════
// PHASE 6: Subscribe to Existing Project (History Test)
// ═══════════════════════════════════════════════════════════════════
Console.WriteLine("\n┌────────────────────────────────────────────────────────────────┐");
Console.WriteLine("│ PHASE 6: Subscribe to Existing Project (History Test)         │");
Console.WriteLine("└────────────────────────────────────────────────────────────────┘");

Console.WriteLine("\n[ACTION] Subscribing to existing project (offset 0 = full history)...");
await hub2.SubscribeProject(projectId, 0);
Console.WriteLine("[SUCCESS] Subscribed");

// Wait a moment for history to be sent
Console.WriteLine("[WAITING] Waiting for history events...");
await Task.Delay(2000);

var historyEventCount = clientHandler2.EventCount;
Console.WriteLine($"\n[RESULT] Received {historyEventCount} events from history");

if (historyEventCount == 0)
{
    Console.WriteLine("[ERROR] ❌ NO HISTORY EVENTS RECEIVED - THIS IS THE BUG!");
}
else
{
    Console.WriteLine("[INFO] History events received:");
    foreach (var msg in clientHandler2.ReceivedMessages)
    {
        var textProp = msg.Properties.FirstOrDefault(p => p.Name == "text" || p.Name == "result" || p.Name == "subtype");
        var contentPreview = textProp?.Value ?? "(structured)";
        if (contentPreview.Length > 60) contentPreview = contentPreview[..60] + "...";
        Console.WriteLine($"  [{msg.Type}] {contentPreview}");
    }

    // Verify history matches original
    if (historyEventCount == phase2EventCount)
    {
        Console.WriteLine($"\n[RESULT] ✅ History event count matches original ({historyEventCount})");
    }
    else
    {
        Console.WriteLine($"\n[RESULT] ⚠️ History count ({historyEventCount}) differs from original ({phase2EventCount})");
    }

    // Check for content (messages with any properties beyond type)
    var messagesWithContent = clientHandler2.ReceivedMessages.Count(m => m.Properties.Count > 1);
    if (messagesWithContent == 0)
    {
        Console.WriteLine("[ERROR] ❌ ALL HISTORY EVENTS HAVE NO CONTENT - PARSING BUG!");
    }
    else
    {
        Console.WriteLine($"[RESULT] ✅ {messagesWithContent} messages have content");
    }
}

// ═══════════════════════════════════════════════════════════════════
// PHASE 7: Resume Project and Interact
// ═══════════════════════════════════════════════════════════════════
Console.WriteLine("\n┌────────────────────────────────────────────────────────────────┐");
Console.WriteLine("│ PHASE 7: Resume Project and Interact                          │");
Console.WriteLine("└────────────────────────────────────────────────────────────────┘");

Console.WriteLine("\n[ACTION] Resuming project...");
await hub2.ResumeProject(projectId);
Console.WriteLine("[SUCCESS] Project resumed");

status = await hub2.GetStatus(projectId);
Console.WriteLine($"[INFO] Project state: {status.State}");

// Wait for project to be ready
await Task.Delay(2000);

// Send new input
var followUpInput = "Now say 'Goodbye!'";
Console.WriteLine($"\n[ACTION] Sending follow-up input: {followUpInput}");
var eventCountBeforeInput = clientHandler2.EventCount;
await hub2.SendInput(projectId, followUpInput);
Console.WriteLine("[SUCCESS] Input sent");

Console.WriteLine("\n[WAITING] Waiting for Claude's response to follow-up...");
clientHandler2.ResetResultFlag();
await WaitForResult(clientHandler2, TimeSpan.FromSeconds(60));

var newEvents = clientHandler2.EventCount - eventCountBeforeInput;
Console.WriteLine($"\n[RESULT] Received {newEvents} new events after follow-up");

if (newEvents > 0)
{
    Console.WriteLine("[INFO] New events:");
    foreach (var msg in clientHandler2.ReceivedMessages.Skip(eventCountBeforeInput))
    {
        var textProp = msg.Properties.FirstOrDefault(p => p.Name == "text" || p.Name == "result" || p.Name == "subtype");
        var contentPreview = textProp?.Value ?? "(structured)";
        if (contentPreview.Length > 60) contentPreview = contentPreview[..60] + "...";
        Console.WriteLine($"  [{msg.Type}] {contentPreview}");
    }
    Console.WriteLine($"\n[RESULT] ✅ Responses arrived after reconnect!");
}
else
{
    Console.WriteLine("[ERROR] ❌ NO NEW EVENTS AFTER FOLLOW-UP INPUT");
}

// ═══════════════════════════════════════════════════════════════════
// CLEANUP
// ═══════════════════════════════════════════════════════════════════
Console.WriteLine("\n┌────────────────────────────────────────────────────────────────┐");
Console.WriteLine("│ CLEANUP                                                        │");
Console.WriteLine("└────────────────────────────────────────────────────────────────┘");

Console.WriteLine("\n[ACTION] Stopping project...");
await hub2.StopProject(projectId);
Console.WriteLine("[ACTION] Unsubscribing...");
await hub2.UnsubscribeProject(projectId);
Console.WriteLine("[ACTION] Disconnecting...");
registration2.Dispose();
await connection2.StopAsync();

// ═══════════════════════════════════════════════════════════════════
// FINAL SUMMARY
// ═══════════════════════════════════════════════════════════════════
Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║                     TEST SUMMARY                             ║");
Console.WriteLine("╠══════════════════════════════════════════════════════════════╣");
Console.WriteLine($"║ Project ID: {projectId,-47} ║");
Console.WriteLine($"║ Phase 2 Events (initial): {phase2EventCount,-33} ║");
Console.WriteLine($"║ Phase 6 Events (history): {historyEventCount,-33} ║");
Console.WriteLine($"║ Phase 7 Events (after resume): {newEvents,-28} ║");
Console.WriteLine("╠══════════════════════════════════════════════════════════════╣");

var allPassed = historyEventCount > 0 &&
                clientHandler2.ReceivedMessages.Any(m => m.Properties.Count > 1) &&
                newEvents > 0;

if (allPassed)
{
    Console.WriteLine("║ RESULT: ✅ ALL TESTS PASSED                                  ║");
}
else
{
    Console.WriteLine("║ RESULT: ❌ SOME TESTS FAILED                                 ║");
}
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

return allPassed ? 0 : 1;

// ═══════════════════════════════════════════════════════════════════
// HELPER METHODS
// ═══════════════════════════════════════════════════════════════════

static async Task<(HubConnection, IProjectHub, ProjectHubClientHandler, IDisposable)> CreateConnection(string serverUrl)
{
    Console.WriteLine($"\n[ACTION] Connecting to server...");

    var connection = new HubConnectionBuilder()
        .WithUrl(serverUrl)
        .WithAutomaticReconnect()
        .Build();

    var hub = connection.CreateHubProxy<IProjectHub>();
    var clientHandler = new ProjectHubClientHandler();
    var registration = connection.Register<IProjectHubClient>(clientHandler);

    await connection.StartAsync();
    Console.WriteLine($"[SUCCESS] Connected! State: {connection.State}");

    return (connection, hub, clientHandler, registration);
}

static async Task WaitForResult(ProjectHubClientHandler handler, TimeSpan timeout)
{
    var cts = new CancellationTokenSource(timeout);
    try
    {
        while (!cts.Token.IsCancellationRequested)
        {
            await Task.Delay(500, cts.Token);
            if (handler.ReceivedResult)
            {
                Console.WriteLine("[INFO] Got result event");
                break;
            }
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("[WARN] Timeout waiting for result");
    }
}

// Client handler implementation
class ProjectHubClientHandler : IProjectHubClient
{
    private readonly List<ClaudeMessage> _messages = [];

    public bool ReceivedResult { get; private set; }
    public int EventCount => _messages.Count;
    public IReadOnlyList<ClaudeMessage> ReceivedMessages => _messages;

    public void ResetResultFlag() => ReceivedResult = false;

    public Task OutputReceived(string projectId, string rawJson)
    {
        var message = new ClaudeMessage(rawJson);
        _messages.Add(message);

        // Extract a preview from the first text property
        var textProp = message.Properties.FirstOrDefault(p =>
            p.Name == "text" || p.Name == "result" || p.Name == "subtype");
        var contentPreview = textProp != null
            ? (textProp.Value.Length > 50 ? textProp.Value[..50] + "..." : textProp.Value)
            : "(structured)";

        Console.WriteLine($"  >> [{message.Type}] {contentPreview}");

        if (message.Type == "result")
        {
            ReceivedResult = true;
        }

        return Task.CompletedTask;
    }

    public Task StatusChanged(string projectId, ProjectStatus status)
    {
        Console.WriteLine($"  >> [STATUS] {projectId} -> {status.State}");
        return Task.CompletedTask;
    }

    public Task ProjectCreated(ProjectStatus status)
    {
        Console.WriteLine($"  >> [CREATED] {status.Id} ({status.Name})");
        return Task.CompletedTask;
    }
}
