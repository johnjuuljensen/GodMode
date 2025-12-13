# Quick Start Guide - GodMode.ProjectFiles

## Installation

Add reference to your project:

```xml
<ProjectReference Include="..\GodMode.ProjectFiles\GodMode.ProjectFiles.csproj" />
```

## Basic Operations

### Create a New Project

```csharp
using var project = ProjectFolder.Create(
    rootPath: @"C:\projects",
    projectId: "my-project",
    name: "My Project"
);
```

### Open an Existing Project

```csharp
using var project = ProjectFolder.Open(@"C:\projects\my-project");
```

### Read Project Status

```csharp
var status = await project.ReadStatusAsync();
Console.WriteLine($"Project: {status.Name}");
Console.WriteLine($"State: {status.State}");
Console.WriteLine($"Tokens: {status.Metrics.InputTokens} in, {status.Metrics.OutputTokens} out");
```

### Update Project Status

```csharp
await project.UpdateStatusAsync(status => status with
{
    State = ProjectState.Running,
    UpdatedAt = DateTime.UtcNow
});
```

### Write Output Events

```csharp
var evt = new OutputEvent(
    Timestamp: DateTime.UtcNow,
    Type: OutputEventType.AssistantOutput,
    Content: "Analysis complete!",
    Metadata: new Dictionary<string, object>
    {
        ["confidence"] = 0.95
    }
);

await project.AppendOutputAsync(evt);
```

### Read Output Events

```csharp
// Read all events
var allEvents = project.ReadAllOutput();

// Read from specific offset (for streaming)
var (newEvents, newOffset) = project.ReadOutputFrom(lastOffset);
```

### Watch for Changes

```csharp
using var watcher = new ProjectFolderWatcher(project);

watcher.OutputEventsReceived += (sender, args) =>
{
    foreach (var evt in args.Events)
    {
        Console.WriteLine($"[{evt.Timestamp:HH:mm:ss}] {evt.Type}: {evt.Content}");
    }
};

watcher.Start();
// ... watcher runs in background ...
watcher.Stop();
```

## Multi-Project Management

### List All Projects

```csharp
var manager = new ProjectManager(@"C:\projects");
var projects = await manager.ListProjectsAsync();

foreach (var proj in projects)
{
    Console.WriteLine($"{proj.Id}: {proj.Name} ({proj.State})");
}
```

### Create Project via Manager

```csharp
using var project = manager.CreateProject("project-1", "My First Project");
```

### Delete Project

```csharp
// Safe delete (fails if running)
await manager.DeleteProjectAsync("project-1");

// Force delete
await manager.DeleteProjectAsync("project-1", force: true);
```

## Validation

### Validate Project Structure

```csharp
var result = ProjectValidator.ValidateProject(@"C:\projects\my-project");

if (result.IsValid)
{
    Console.WriteLine("Project is valid!");
}
else
{
    Console.WriteLine("Errors:");
    foreach (var error in result.Errors)
        Console.WriteLine($"  - {error}");
}

if (result.Warnings.Count > 0)
{
    Console.WriteLine("Warnings:");
    foreach (var warning in result.Warnings)
        Console.WriteLine($"  - {warning}");
}
```

### Repair Project

```csharp
if (ProjectValidator.TryRepairProject(@"C:\projects\my-project"))
{
    Console.WriteLine("Project repaired successfully!");
}
```

## Advanced Usage

### Batch Write Events

```csharp
using var writer = new JsonlWriter(project.OutputFilePath);

var events = new[]
{
    new OutputEvent(DateTime.UtcNow, OutputEventType.System, "Starting..."),
    new OutputEvent(DateTime.UtcNow, OutputEventType.ToolUse, "git status"),
    new OutputEvent(DateTime.UtcNow, OutputEventType.ToolResult, "On branch main...")
};

await writer.AppendBatchAsync(events);
```

### Stream Processing

```csharp
long offset = 0;

while (true)
{
    var (events, newOffset) = project.ReadOutputFrom(offset);

    foreach (var evt in events)
    {
        ProcessEvent(evt);
    }

    if (newOffset == offset)
    {
        // No new data, wait
        await Task.Delay(1000);
    }
    else
    {
        offset = newOffset;
    }
}
```

### Session Management

```csharp
// Store Claude session ID for resumption
await project.SetSessionIdAsync("sess_abc123");

// Retrieve session ID
var sessionId = await project.GetSessionIdAsync();
if (sessionId != null)
{
    Console.WriteLine($"Resuming session: {sessionId}");
}
```

### Metrics HTML

```csharp
// Write metrics visualization
var html = GenerateMetricsHtml(status);
await project.WriteMetricsHtmlAsync(html);

// Read metrics
var metricsHtml = await project.ReadMetricsHtmlAsync();
if (metricsHtml != null)
{
    // Display or serve HTML
}
```

## Common Patterns

### Project Lifecycle

```csharp
// 1. Create
using var project = ProjectFolder.Create(rootPath, id, name);

// 2. Initialize
await project.UpdateStatusAsync(s => s with { State = ProjectState.Running });
await project.SetSessionIdAsync(sessionId);

// 3. Process
var watcher = new ProjectFolderWatcher(project);
watcher.OutputEventsReceived += HandleOutputEvents;
watcher.Start();

// 4. Complete
await project.UpdateStatusAsync(s => s with
{
    State = ProjectState.Idle,
    Metrics = s.Metrics with { Duration = elapsed }
});

// 5. Cleanup
watcher.Dispose();
project.Dispose();
```

### Error Handling

```csharp
try
{
    using var project = ProjectFolder.Open(path);
    var status = await project.ReadStatusAsync();
}
catch (DirectoryNotFoundException ex)
{
    Console.WriteLine($"Project not found: {ex.Message}");
}
catch (CorruptProjectException ex)
{
    Console.WriteLine($"Project corrupted: {ex.Message}");
    if (ProjectValidator.TryRepairProject(path))
    {
        // Retry operation
    }
}
catch (JsonException ex)
{
    Console.WriteLine($"Invalid JSON: {ex.Message}");
}
```

### Safe Status Updates

```csharp
// Atomic update with retry
async Task<ProjectStatus> UpdateWithRetry(ProjectFolder project, Func<ProjectStatus, ProjectStatus> update)
{
    const int maxRetries = 3;
    for (int i = 0; i < maxRetries; i++)
    {
        try
        {
            return await project.UpdateStatusAsync(update);
        }
        catch (IOException) when (i < maxRetries - 1)
        {
            await Task.Delay(100 * (i + 1));
        }
    }
    throw new InvalidOperationException("Failed to update status after retries");
}
```

## Best Practices

1. **Always use `using` statements** for ProjectFolder and Watcher to ensure proper disposal
2. **Use async methods** when possible for better scalability
3. **Track offsets** when reading output to avoid re-processing events
4. **Validate projects** before critical operations
5. **Handle exceptions** appropriately (file I/O can fail)
6. **Update status.UpdatedAt** whenever changing status
7. **Use ProjectManager** for multi-project scenarios
8. **Set session IDs** to enable Claude session resumption

## Troubleshooting

### Project won't open
```csharp
if (!ProjectValidator.IsValidProjectPath(path))
{
    var result = ProjectValidator.ValidateProject(path);
    // Check result.Errors and result.Warnings
}
```

### Events not appearing in watcher
```csharp
// Manually trigger check
watcher.CheckForChanges();

// Or reset offset
watcher.ResetOffset(0);
```

### Status.json corrupted
```csharp
// Validation will catch this
var result = ProjectValidator.ValidateProject(path);
// Cannot automatically repair status.json - must recreate
```

### File access conflicts
```csharp
// JsonlReader uses FileShare.ReadWrite
// Multiple readers are safe
// JsonlWriter uses locks for thread safety
```

## Performance Tips

1. Use batch append for multiple events
2. Read from offsets instead of full file reads
3. Use JSON source generators (automatic via ProjectJsonContext)
4. Dispose watchers when not needed
5. Limit watcher polling frequency if needed

## See Also

- [README.md](README.md) - Comprehensive documentation
- [IMPLEMENTATION_SUMMARY.md](IMPLEMENTATION_SUMMARY.md) - Technical details
