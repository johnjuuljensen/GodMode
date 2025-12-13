# Claude Autonomous Development System - Architecture Specification

## Overview

A system for managing multiple autonomous Claude Code instances across local machines and GitHub Codespaces. Users interact through a cross-platform MAUI application that connects to lightweight SignalR servers running alongside Claude processes. Projects are organized as folders with standardized file structures for input, output, and state.

---

## Core Concepts

### Project

A discrete unit of work being performed by a Claude Code instance. Represented as a folder containing:
- `status.json` - Current state, metadata, metrics
- `input.jsonl` - Append-only log of user inputs
- `output.jsonl` - Append-only log of Claude outputs
- Working files (cloned repo, generated code, etc.)

### Host

An environment where projects run. Implementations:
- **Codespace Host** - GitHub Codespace with SignalR server
- **Local Host** - Local machine, either in-process or via local SignalR server

### Profile

A named configuration grouping related accounts and hosts. Examples: "Private" (personal projects), "Outbound" (client work), "Mega" (work projects).

---

## Component 1: Project Folder Structure

Each project lives in a dedicated folder. The folder name is the project ID.

```
/projects/{project-id}/
├── status.json
├── input.jsonl
├── output.jsonl
├── metrics.html          # Optional, generated
├── session-id            # Claude session ID for resumption
└── work/                 # Working directory for Claude
    └── (cloned repo, generated files, etc.)
```

### status.json

```
{
  "id": "string",
  "name": "string",
  "state": "idle" | "running" | "waiting_input" | "error" | "stopped",
  "createdAt": "ISO8601",
  "updatedAt": "ISO8601",
  "repoUrl": "string?",
  "currentQuestion": "string?",
  "metrics": {
    "inputTokens": number,
    "outputTokens": number,
    "toolCalls": number,
    "duration": "timespan",
    "costEstimate": number
  },
  "git": {
    "branch": "string?",
    "lastCommit": "string?",
    "uncommittedChanges": number,
    "untrackedFiles": number
  },
  "tests": {
    "total": number,
    "passed": number,
    "failed": number,
    "lastRun": "ISO8601?"
  },
  "outputOffset": number    # Byte offset for resume
}
```

### input.jsonl / output.jsonl

Each line is a JSON object with consistent structure for type-safe parsing:

```
{
  "timestamp": "ISO8601",
  "type": "user_input" | "assistant_output" | "thinking" | "tool_use" | "tool_result" | "error" | "system",
  "content": "string",
  "metadata": { ... }    # Type-specific additional data
}
```

---

## Component 2: MAUI Application

Cross-platform application (Windows, macOS, Android) serving as the control plane.

### Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  MAUI App                                                       │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  UI Layer                                                │   │
│  │  - Profile Selector                                      │   │
│  │  - Host/Codespace Browser                                │   │
│  │  - Project List                                          │   │
│  │  - Project Detail (Status Board + Chat)                  │   │
│  └─────────────────────────────────────────────────────────┘   │
│                            │                                    │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  Service Layer                                           │   │
│  │  - ProfileService                                        │   │
│  │  - HostConnectionService                                 │   │
│  │  - ProjectService                                        │   │
│  │  - NotificationService                                   │   │
│  └─────────────────────────────────────────────────────────┘   │
│                            │                                    │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  Host Abstraction Layer                                  │   │
│  │                                                          │   │
│  │  IHostProvider                                           │   │
│  │    ├─ GitHubCodespaceProvider                            │   │
│  │    ├─ LocalFolderProvider                                │   │
│  │    └─ (Future: SSH, Azure, etc.)                         │   │
│  │                                                          │   │
│  │  IProjectConnection                                      │   │
│  │    ├─ SignalRProjectConnection                           │   │
│  │    └─ LocalProjectConnection (direct file access)        │   │
│  └─────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

### Profiles

Stored locally. Each profile contains:
- Name
- Associated accounts (GitHub accounts, local folder paths)
- Default settings (preferred host, etc.)

```
{
  "profiles": [
    {
      "name": "Private",
      "accounts": [
        { "type": "github", "username": "john-personal", "token": "encrypted" },
        { "type": "local", "path": "/home/john/claude-projects" }
      ]
    },
    {
      "name": "Mega",
      "accounts": [
        { "type": "github", "username": "john-megacorp" }
      ]
    }
  ]
}
```

### Host Provider Interface

```
IHostProvider
  - string Type { get; }
  - Task<IEnumerable<HostInfo>> ListHostsAsync()
  - Task<HostStatus> GetHostStatusAsync(hostId)
  - Task StartHostAsync(hostId)
  - Task StopHostAsync(hostId)
  - Task<IProjectConnection> ConnectAsync(hostId)
```

### GitHub Codespace Provider

- Uses GitHub API (via Octokit or raw HTTP) to list/start/stop codespaces
- Credentials stored per-profile
- Returns codespace name, state, URLs
- Connection establishes SignalR link to server running on codespace

### Local Folder Provider

- Configured with one or more root paths
- Each path treated as a host
- Can either:
  - Connect directly (watch files in-process, spawn Claude directly)
  - Connect to local SignalR server (if running)

### Project Connection Interface

```
IProjectConnection
  - Task<IEnumerable<ProjectSummary>> ListProjectsAsync()
  - Task<ProjectStatus> GetStatusAsync(projectId)
  - Task<ProjectDetail> CreateProjectAsync(name, repoUrl?, initialPrompt)
  - Task SendInputAsync(projectId, input)
  - Task StopProjectAsync(projectId)
  - IObservable<OutputEvent> SubscribeOutput(projectId, fromOffset)
  - Task<string> GetMetricsHtmlAsync(projectId)
  - void Disconnect()
```

### UI Views

**Main View**
- Profile selector (dropdown/tabs)
- List of hosts for selected profile, with status indicators
- "Add host" action

**Host View**
- Host status (running, stopped, starting)
- Start/Stop actions for Codespaces
- List of projects on this host
- "New Project" action

**Project View**
Split view:

*Status Board (top/side)*
- State indicator with color coding
- Metrics display (tokens, cost, duration)
- Git status (branch, uncommitted changes)
- Test results (pass/fail counts)
- Quick actions (stop, restart)

*Chat Interface (main)*
- Scrollable output log with syntax highlighting
- Visual distinction between: assistant output, thinking, tool use, errors
- Input field at bottom
- Current question highlighted if waiting for input
- Resume position indicator on reconnect

### Notifications

- System notifications when project needs input
- Badge counts for projects awaiting attention
- Optional sound/vibration on mobile

### Offline/Reconnection Behavior

- Store last known state locally
- On reconnect, resume output stream from stored offset
- Queue inputs if disconnected, send on reconnect
- Visual indicator of connection state

---

## Component 3: SignalR Server

Lightweight .NET server running on each host (Codespace or local machine). Manages Claude processes and provides real-time communication with clients.

### Responsibilities

- Start/stop Claude Code processes using CliWrap
- Pipe input from clients to Claude stdin
- Watch output.jsonl and stream new content to subscribed clients
- Maintain and update status.json
- Generate metrics.html
- Handle multiple simultaneous projects
- Handle multiple simultaneous client connections
- Persist state to survive restarts

### Hub API

**Server → Client (events)**
```
OutputReceived(projectId, outputEvent)
StatusChanged(projectId, status)
ProjectCreated(project)
ProjectRemoved(projectId)
```

**Client → Server (invocations)**
```
Task<ProjectSummary[]> ListProjects()
Task<ProjectStatus> GetStatus(projectId)
Task<ProjectDetail> CreateProject(name, repoUrl?, initialPrompt)
Task SendInput(projectId, input)
Task StopProject(projectId)
Task SubscribeProject(projectId, outputOffset)
Task UnsubscribeProject(projectId)
Task<string> GetMetricsHtml(projectId)
```

### Process Management

Using CliWrap:

```
// Conceptual, not implementation
var claude = Cli.Wrap("claude")
    .WithArguments(["--output-format", "stream-json", "--session-id", sessionId, prompt])
    .WithWorkingDirectory(projectWorkDir)
    .WithStandardInputPipe(PipeSource.FromStream(inputStream))
    .WithStandardOutputPipe(PipeTarget.ToDelegate(OnOutput));
```

### File Watching

- Use FileSystemWatcher or polling to detect appends to output.jsonl
- On change, read new bytes from last known offset
- Parse complete lines as JSON events
- Broadcast to subscribed clients
- Update status.json when relevant events occur (input requests, completion, errors)

### Status Updates

Triggered by output events:
- `input_request` → state = "waiting_input", extract question
- `complete` → state = "idle"
- `error` → state = "error"
- Periodically update metrics (token counts parsed from stream)

Git status polling:
- Run `git status --porcelain` periodically or on demand
- Run `git log -1 --format=%H` for last commit
- Update status.json

Test status:
- If project has detectable test runner, parse results
- Or expose as manual update via Claude tool use

### Startup Recovery

On server start:
1. Scan projects folder
2. For each project with state "running" or "waiting_input":
   - Attempt to reattach to process if still running
   - Or mark as "stopped" / "error"
3. Load last known offsets

### Authentication

For Codespaces: rely on Codespace port forwarding authentication (GitHub session cookie). Private port means only authenticated user can connect.

For local: optional, could be open (localhost only) or require shared secret.

---

## Component 4: Shared Library

Common types used by both MAUI app and SignalR server.

### Message Types

```
// Base event structure
record OutputEvent(
    DateTime Timestamp,
    OutputEventType Type,
    string Content,
    Dictionary<string, object>? Metadata
);

enum OutputEventType
{
    UserInput,
    AssistantOutput,
    Thinking,
    ToolUse,
    ToolResult,
    Error,
    System
}

// Project summaries
record ProjectSummary(
    string Id,
    string Name,
    ProjectState State,
    DateTime UpdatedAt,
    string? CurrentQuestion
);

record ProjectStatus(
    string Id,
    string Name,
    ProjectState State,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? RepoUrl,
    string? CurrentQuestion,
    ProjectMetrics Metrics,
    GitStatus? Git,
    TestStatus? Tests,
    long OutputOffset
);

record ProjectMetrics(
    long InputTokens,
    long OutputTokens,
    int ToolCalls,
    TimeSpan Duration,
    decimal CostEstimate
);

record GitStatus(
    string? Branch,
    string? LastCommit,
    int UncommittedChanges,
    int UntrackedFiles
);

record TestStatus(
    int Total,
    int Passed,
    int Failed,
    DateTime? LastRun
);

enum ProjectState
{
    Idle,
    Running,
    WaitingInput,
    Error,
    Stopped
}

// Creation request
record CreateProjectRequest(
    string Name,
    string? RepoUrl,
    string InitialPrompt
);
```

---

## Deployment

### Codespace

The SignalR server runs as part of the dev container setup.

**devcontainer.json additions:**
- Install .NET runtime
- Build/publish server on create
- Start server on postStartCommand
- Forward server port (private visibility)

**Server location:** `/workspaces/.claude-control/server/`

**Projects location:** `/workspaces/projects/`

### Local

User runs server manually or via system service. Projects folder is configurable.

---

## Security Considerations

- GitHub tokens stored encrypted in app local storage
- Codespace ports are private (GitHub auth required)
- No sensitive data in shared library types
- Server does not expose filesystem beyond projects folder

---

## Future Extensions

- SSH host provider (connect to arbitrary Linux machines)
- Azure/AWS VM providers
- Shared/team projects (multiple users, single project)
- Project templates (preset configurations)
- Integration with GitHub Issues (link projects to issues)
- Cost tracking and budgets
- Scheduled tasks (run Claude at specific times)

---

## Implementation Order

1. **Shared library** - Define all types
2. **SignalR server** - Core functionality, single project
3. **SignalR server** - Multi-project, file watching, status updates
4. **MAUI app** - Basic structure, local folder provider
5. **MAUI app** - SignalR connection, project list, chat view
6. **MAUI app** - GitHub provider, codespace listing
7. **Integration** - Dev container setup, end-to-end flow
8. **Polish** - Notifications, reconnection, metrics visualization
9. **Android** - Platform-specific adaptations, background service