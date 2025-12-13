# GodMode.ProjectFiles

A .NET 9 class library providing utilities for working with project folders and their standard file structures in the Claude Autonomous Development System.

## Overview

This library manages the standardized folder structure for Claude Code projects, handling JSON status files, JSONL event streams, and file watching capabilities.

## Project Folder Structure

Each project is represented as a folder with the following structure:

```
/projects/{project-id}/
├── status.json           # Current state, metadata, metrics
├── input.jsonl          # Append-only log of user inputs
├── output.jsonl         # Append-only log of Claude outputs
├── metrics.html         # Optional, generated metrics visualization
├── session-id           # Claude session ID for resumption
└── work/                # Working directory for Claude
    └── (project files)
```

## Core Components

### 1. ProjectFolder

The main class for managing project folders. Provides methods for:

- Creating new projects with initial structure
- Opening existing projects
- Reading/writing status.json
- Appending to input.jsonl and output.jsonl
- Managing session IDs
- Reading metrics

**Usage:**

```csharp
// Create a new project
using var project = ProjectFolder.Create(
    rootPath: "/projects",
    projectId: "my-project",
    name: "My Project",
    repoUrl: "https://github.com/user/repo"
);

// Write initial status
var status = await project.ReadStatusAsync();
status = status with { State = ProjectState.Running };
await project.WriteStatusAsync(status);

// Append output event
var evt = new OutputEvent(
    DateTime.UtcNow,
    OutputEventType.AssistantOutput,
    "Hello from Claude!",
    null
);
await project.AppendOutputAsync(evt);

// Read new events from offset
var (events, newOffset) = project.ReadOutputFrom(0);
```

### 2. ProjectManager

Manages multiple projects within a root directory.

**Usage:**

```csharp
var manager = new ProjectManager("/projects");

// List all projects
var projects = await manager.ListProjectsAsync();

// Create a project
using var project = manager.CreateProject("project-1", "My Project");

// Open existing project
using var existing = manager.OpenProject("project-1");

// Delete a project
await manager.DeleteProjectAsync("project-1");
```

### 3. JsonlReader

Utility for reading JSONL (JSON Lines) files incrementally.

**Features:**
- Read all events from a file
- Read from a specific byte offset (for streaming)
- Thread-safe file access with FileShare.ReadWrite
- Automatic line-by-line JSON parsing

**Usage:**

```csharp
// Read all events
var allEvents = JsonlReader.ReadAll("/path/to/output.jsonl");

// Read from offset (for resume/streaming)
var (newEvents, newOffset) = JsonlReader.ReadFrom("/path/to/output.jsonl", lastOffset);

// Get current file size
var size = JsonlReader.GetFileSize("/path/to/output.jsonl");
```

### 4. JsonlWriter

Thread-safe writer for JSONL files.

**Features:**
- Thread-safe append operations using SemaphoreSlim
- Automatic directory creation
- Batch append support
- Both sync and async APIs

**Usage:**

```csharp
using var writer = new JsonlWriter("/path/to/output.jsonl");

// Append single event
await writer.AppendAsync(evt);

// Append batch of events
var events = new[] { evt1, evt2, evt3 };
await writer.AppendBatchAsync(events);

// Get current offset
var offset = writer.GetCurrentOffset();
```

### 5. ProjectFolderWatcher

FileSystemWatcher wrapper for monitoring output.jsonl changes in real-time.

**Features:**
- Automatic file change detection
- Event-based notifications of new content
- Byte offset tracking
- Background processing queue
- Error handling and recovery

**Usage:**

```csharp
using var project = ProjectFolder.Open("/projects/my-project");
using var watcher = new ProjectFolderWatcher(project, startOffset: 0);

// Subscribe to events
watcher.OutputEventsReceived += (sender, args) =>
{
    foreach (var evt in args.Events)
    {
        Console.WriteLine($"[{evt.Type}] {evt.Content}");
    }
    Console.WriteLine($"New offset: {args.NewOffset}");
};

// Start watching
watcher.Start();

// ... watcher runs in background ...

// Manually check for changes
watcher.CheckForChanges();

// Stop watching
watcher.Stop();
```

### 6. ProjectJsonContext

JSON source generator context for high-performance serialization.

**Features:**
- AOT-compatible source generation
- Optimized serialization for all project types
- Camel case property naming
- Null value handling

**Usage:**

```csharp
// Serialization uses the context automatically
var json = JsonSerializer.Serialize(status, ProjectJsonContext.Default.ProjectStatus);
var status = JsonSerializer.Deserialize<ProjectStatus>(json, ProjectJsonContext.Default.ProjectStatus);
```

## Data Models

All data models are defined in **GodMode.Shared** and include:

### OutputEvent
Represents a single event in the JSONL stream.

```csharp
public record OutputEvent(
    DateTime Timestamp,
    OutputEventType Type,      // UserInput, AssistantOutput, Thinking, ToolUse, ToolResult, Error, System
    string Content,
    Dictionary<string, object>? Metadata = null
);
```

### ProjectStatus
Complete project status corresponding to status.json.

```csharp
public record ProjectStatus(
    string Id,
    string Name,
    ProjectState State,        // Idle, Running, WaitingInput, Error, Stopped
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? RepoUrl,
    string? CurrentQuestion,
    ProjectMetrics Metrics,
    GitStatus? Git,
    TestStatus? Tests,
    long OutputOffset          // Byte offset for resume
);
```

### ProjectMetrics
Execution metrics for a project.

```csharp
public record ProjectMetrics(
    long InputTokens,
    long OutputTokens,
    int ToolCalls,
    TimeSpan Duration,
    decimal CostEstimate
);
```

### GitStatus
Git repository status information.

```csharp
public record GitStatus(
    string? Branch,
    string? LastCommit,
    int UncommittedChanges,
    int UntrackedFiles
);
```

### TestStatus
Test execution results.

```csharp
public record TestStatus(
    int Total,
    int Passed,
    int Failed,
    DateTime? LastRun
);
```

## Exception Handling

### ProjectFolderException
Base exception for project folder operations.

### CorruptProjectException
Thrown when a project folder structure is invalid or corrupted.

**Example:**

```csharp
try
{
    using var project = ProjectFolder.Open("/projects/invalid");
}
catch (CorruptProjectException ex)
{
    Console.WriteLine($"Project {ex.ProjectId} is corrupted: {ex.Message}");
}
```

## Thread Safety

The library is designed with thread safety in mind:

- **JsonlWriter**: Uses SemaphoreSlim for thread-safe writes
- **ProjectFolder**: Status operations are protected with locks
- **JsonlReader**: Opens files with FileShare.ReadWrite for concurrent access
- **ProjectFolderWatcher**: Uses concurrent queue for event processing

## Performance Considerations

1. **JSON Source Generators**: Used for optimal serialization performance
2. **Incremental Reading**: Read from byte offsets instead of full file reads
3. **Async APIs**: All I/O operations have async variants
4. **Batch Operations**: JsonlWriter supports batch appends to reduce I/O
5. **Lazy Enumeration**: JsonlReader uses yield return for memory efficiency

## Dependencies

- **GodMode.Shared**: Core data models and enums
- **.NET 9.0**: Latest .NET version for performance and features
- **System.Text.Json**: High-performance JSON serialization

## Building

```bash
dotnet build GodMode.ProjectFiles.csproj
```

## Testing Example

```csharp
// Create a test project
var testRoot = Path.Combine(Path.GetTempPath(), "test-projects");
using var project = ProjectFolder.Create(testRoot, "test-1", "Test Project");

// Write some events
var evt = new OutputEvent(DateTime.UtcNow, OutputEventType.System, "Starting...", null);
await project.AppendOutputAsync(evt);

// Update status
await project.UpdateStatusAsync(s => s with
{
    State = ProjectState.Running,
    UpdatedAt = DateTime.UtcNow
});

// Watch for changes
using var watcher = new ProjectFolderWatcher(project);
watcher.OutputEventsReceived += (s, e) =>
{
    Console.WriteLine($"Received {e.Events.Count} events");
};
watcher.Start();

// Clean up
Directory.Delete(testRoot, true);
```

## Design Patterns

- **Disposable Pattern**: All resource-holding classes implement IDisposable
- **Factory Methods**: Static Create/Open methods for ProjectFolder
- **Record Types**: Immutable data structures for thread safety
- **Event-Based Async**: Watcher uses events for notifications
- **Repository Pattern**: ProjectManager provides high-level project operations

## Future Enhancements

Potential additions:
- Transaction support for atomic status updates
- Compression for archived projects
- Project validation and repair utilities
- Migration tools for version upgrades
- Performance metrics and diagnostics
