# GodMode.ProjectFiles

A .NET 10 class library for the files GodMode keeps on disk: a root's working folders, and each session's state in them, with its JSON status and JSONL streams.

## Project Folder Structure

Every session has a working folder inside its root, where Claude works, and GodMode keeps the session's state in that folder's `.godmode/sessions/<id>/`. The same layout serves every kind of root: a worktree is a working folder with one session. The session's opaque ID, on the server, is `{profile}/{root}/{id}`.

```
{root}/{project-folder}/
├── .godmode/
│   ├── .gitignore               # "*": keeps .godmode out of git
│   └── sessions/
│       └── {id}/                # e.g. 260929-feat-left-list-k7q2
│           ├── status.json      # Current state, metadata, metrics, kind
│           ├── settings.json    # The session's settings (skip-permissions, permission mode, action)
│           ├── input.jsonl      # Append-only log of user inputs
│           ├── output.jsonl     # Append-only log of Claude outputs
│           ├── output-generation # Changes when output.jsonl starts over
│           └── session-id       # Claude's session GUID for --resume (not the id)
└── (project files)              # Claude's working directory
```

The old flat layout (`.godmode/status.json` directly in `.godmode/`) is no session: nothing here reads it.

## Core Components

### 1. SessionState

Where a session's state is, and its id:

- `SessionState.Id(createdAt, kind, name, suffix)`: `yymmdd-{kind}-{slug}-{suffix}`. The kind and slug are lowercase `[a-z0-9-]` (`Kind`, `Slug`; `æ`/`ø`/`å` spelled `ae`/`oe`/`aa`, other accents dropped), the kind at most 12 characters (`session` when it has none), the slug at most 24 (left out when the name has none), the suffix 4 random base32 characters (`NewSuffix`). The id is unique only by chance: the server checks its root.
- `SessionState.IsId(id)`: whether a folder name is an id in that form. No other folder in `sessions/` is a session.
- `SessionState.PathOf(workingFolder, id)`: `.godmode/sessions/{id}/`.
- `SessionState.List(workingFolder)`: the ids of the sessions in a working folder, each folder in `sessions/` that is an id and has a `status.json`.
- `SessionState.Create(workingFolder, id)`: makes the state folder, with the folder's `.godmode/.gitignore` first and empty `input.jsonl` and `output.jsonl`.

```csharp
var id = SessionState.Id(DateTime.Now, "feat", "Left list", SessionState.NewSuffix()); // 260929-feat-left-list-k7q2
var state = SessionState.Create("/roots/app/left-list", id);                         // /roots/app/left-list/.godmode/sessions/260929-feat-left-list-k7q2
```

### 2. ProjectFolder

A root's working folders, and one session's state in one:

- `ProjectFolder.Create(rootPath, folderName)` makes a new working folder (`FOLDER_EXISTS` when it is there); `Reuse` takes an existing one. Neither writes a session's state: the server does that once the session's id is final.
- `ProjectFolder.ValidateFolderName`, `IsReservedFolderName`: a working folder is a folder of its own in the root, not one of the root's own (`.godmode-root`, `logs`, `.archived`), and a name Windows keeps as it is.
- `ProjectFolder.EnsureGitIgnore(workingFolder)`: `.godmode/.gitignore` ignores everything.
- `ProjectFolder.Open(workingFolder, sessionId)`: one session's state, to read and write its `status.json` and append to its JSONL streams.

```csharp
using var session = ProjectFolder.Open("/roots/app/left-list", "260929-feat-left-list-k7q2");

var status = await session.ReadStatusAsync();
await session.WriteStatusAsync(status with { State = ProjectState.Running });

await session.AppendOutputAsync(new OutputEvent(DateTime.UtcNow, OutputEventType.AssistantOutput, "Hello from Claude!", null));
var (events, newOffset) = session.ReadOutputFrom(0);
```

### 3. ProjectManager

Named project roots, and the sessions in their working folders:

```csharp
var manager = new ProjectManager(new Dictionary<string, string> { ["work"] = "/projects" });

// Every session in the root's working folders (its own folders left out)
foreach (var (workingFolder, sessionId) in manager.ListSessions("work")) { /* ... */ }

// Whether an id is taken in the root: the server's check that an id is unique within its root
var taken = manager.HasSession("work", "260929-feat-left-list-k7q2");

// A display name as a folder name: "My fix" is my_fix
var folder = ProjectManager.ConvertNameToPath("My fix");
```
### 4. JsonlReader

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

### 5. JsonlWriter

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

### 6. ProjectFolderWatcher

FileSystemWatcher wrapper for monitoring output.jsonl changes in real-time.

**Features:**
- Automatic file change detection
- Event-based notifications of new content
- Byte offset tracking
- Background processing queue
- Error handling and recovery

**Usage:**

```csharp
using var project = ProjectFolder.Open("/projects/my-project", "260929-feat-my-project-k7q2");
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

### 7. ProjectJsonContext

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
    using var project = ProjectFolder.Open("/projects/invalid", "260929-feat-invalid-k7q2");
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
// A test session: a working folder, and its state with a status.json
var testRoot = Path.Combine(Path.GetTempPath(), "test-projects");
var folder = ProjectFolder.Create(testRoot, "test-1");
var id = SessionState.Id(DateTime.Now, "test", "Test Project", SessionState.NewSuffix());
var state = SessionState.Create(folder, id);
File.WriteAllText(Path.Combine(state, SessionState.StatusFileName), JsonSerializer.Serialize(initialStatus, ProjectJsonContext.Default.ProjectStatus));
using var project = ProjectFolder.Open(folder, id);

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
- **Factory Methods**: Static Create/Reuse (a working folder) and Open (a session's state) on ProjectFolder
- **Record Types**: Immutable data structures for thread safety
- **Event-Based Async**: Watcher uses events for notifications
- **Repository Pattern**: ProjectManager provides high-level project operations

## Future Enhancements

Potential additions:
- Transaction support for atomic status updates
- Project validation and repair utilities
- Performance metrics and diagnostics
