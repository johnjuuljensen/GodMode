# GodMode.Server

SignalR server for the Claude Autonomous Development System. This lightweight .NET server manages Claude Code processes and provides real-time communication with clients.

## Features

- **Real-time Communication**: SignalR hub for bidirectional communication
- **Process Management**: Spawn and control Claude Code processes
- **Config-Driven Project Roots**: Each root directory defines its own creation workflow via `.godmode-root.json`
- **Script-Based Bootstrap**: VCS-agnostic — all setup/bootstrap logic lives in scripts, not server code
- **Cross-Platform Scripts**: Extensionless script references resolve to `.ps1` on Windows, `.sh` on Linux
- **State Persistence**: Save and recover project state across restarts
- **Git Integration**: Track git status and changes
- **Metrics Tracking**: Monitor tokens, cost, and performance

## Configuration

### appsettings.json

```json
{
  "ProjectRoots": {
    "default": "projects",
    "work": "C:\\Users\\me\\work\\projects"
  },
  "ClaudeConfigDir": "C:\\Users\\me\\.claude",
  "Urls": "http://0.0.0.0:31337"
}
```

`ProjectRoots` maps logical names to directory paths. Each root can optionally contain a `.godmode-root.json` file to customize the creation workflow. Roots without the config file get a default form (project name + prompt).

## Project Roots

### .godmode-root.json

Place this file in a project root directory to configure how projects are created there. The server re-reads it on each operation — no restart needed.

```json
{
  "description": "Human-readable description shown in the UI",
  "environment": { "KEY": "value" },
  "inputSchema": { ... },
  "setup": ["scripts/prepare"],
  "bootstrap": ["scripts/init"],
  "teardown": ["scripts/cleanup"],
  "claudeArgs": ["--append-system-prompt", "Extra instructions"],
  "nameTemplate": "{caseId}",
  "promptTemplate": "Fix {caseId}. {prompt}"
}
```

All fields are optional. When the file is missing or empty, the server uses a default schema with `name` and `prompt` fields.

### Fields

| Field | Description |
|-------|-------------|
| `description` | Shown in the UI when selecting a project root |
| `environment` | Env vars set for scripts and passed to Claude processes |
| `inputSchema` | JSON Schema defining the creation form (see below) |
| `setup` | Scripts run before project folder is created (working dir = root) |
| `bootstrap` | Scripts run after project folder is created (working dir = project) |
| `teardown` | Scripts run when a project is deleted |
| `claudeArgs` | Extra CLI arguments appended when starting Claude |
| `nameTemplate` | Derive project name from inputs, e.g. `"{caseId}"` |
| `promptTemplate` | Derive initial prompt from inputs, e.g. `"Fix {caseId}. {prompt}"` |

### Input Schema

A JSON Schema subset defining what the creation form looks like. Supported types:

- `string` — text input
- `string` + `"x-multiline": true` — multiline text area
- `string` + `"enum": [...]` — dropdown/combobox
- `boolean` — toggle/checkbox

```json
{
  "type": "object",
  "properties": {
    "name": { "type": "string", "title": "Project Name" },
    "prompt": { "type": "string", "title": "Task Description", "x-multiline": true },
    "skipPermissions": {
      "type": "boolean",
      "title": "Skip Permissions",
      "description": "Start Claude with --dangerously-skip-permissions",
      "default": "true"
    }
  },
  "required": ["name", "prompt"]
}
```

The `name` and `prompt` keys have special meaning — they're used as the project name and initial Claude prompt unless overridden by `nameTemplate`/`promptTemplate`.

### Scripts

Scripts are the abstraction layer for all VCS and setup operations. The server doesn't know about git, mercurial, or any other tool.

**Cross-platform**: Specify scripts without extension in the config. The server resolves to the right file based on OS:
- Windows: tries `.ps1`, `.cmd`, `.bat`
- Linux/Mac: tries `.sh`

```
my-project-root/
├── .godmode-root.json
└── .godmode-scripts/
    ├── init-git.sh      ← Linux
    └── init-git.ps1     ← Windows
```

Config references: `".godmode-scripts/init-git"` — works on both platforms.

If a script is specified with an explicit extension (e.g. `"scripts/setup.ps1"`), it's used as-is.

**Environment variables** available to all scripts:

| Variable | Description |
|----------|-------------|
| `GODMODE_ROOT_PATH` | Root directory path |
| `GODMODE_PROJECT_PATH` | Project directory path |
| `GODMODE_PROJECT_ID` | Folder name / project ID |
| `GODMODE_PROJECT_NAME` | Display name |
| `GODMODE_INPUT_*` | All form inputs (key uppercased, e.g. `GODMODE_INPUT_CASE_ID`) |
| *(from `environment`)* | All vars from the root config's `environment` block |

Script stdout is streamed to the client as creation progress. Non-zero exit code aborts creation.

### Examples

**Scratch space** (no config needed):
```json
{
  "description": "Ad-hoc Claude tasks"
}
```

**Git-initialized projects**:
```json
{
  "description": "Ad-hoc tasks with git",
  "bootstrap": [".godmode-scripts/init-git"],
  "claudeArgs": ["--append-system-prompt", "Publish to github.com/myuser"]
}
```

**Git worktree workflow**:
```json
{
  "description": "Worktrees for my-app",
  "environment": { "REPO_URL": "https://github.com/user/my-app.git" },
  "inputSchema": {
    "type": "object",
    "properties": {
      "name": { "type": "string", "title": "Branch Name" },
      "prompt": { "type": "string", "title": "Task Description", "x-multiline": true }
    },
    "required": ["name", "prompt"]
  },
  "setup": [".godmode-scripts/ensure-bare-repo"],
  "bootstrap": [".godmode-scripts/create-worktree"]
}
```

**Case management**:
```json
{
  "description": "Fix tasks from Jira",
  "environment": { "JIRA_URL": "https://myco.atlassian.net" },
  "inputSchema": {
    "type": "object",
    "properties": {
      "caseId": { "type": "string", "title": "Case ID" },
      "prompt": { "type": "string", "title": "Additional Instructions", "x-multiline": true, "default": "Fix the issue" }
    },
    "required": ["caseId"]
  },
  "nameTemplate": "{caseId}",
  "promptTemplate": "Fix {caseId}. {prompt}",
  "setup": [".godmode-scripts/ensure-bare-repo"],
  "bootstrap": [".godmode-scripts/setup-worktree", ".godmode-scripts/fetch-jira-context"]
}
```

## Project Folder Structure

Each project is stored in a folder under its root:

```
/root/{project-id}/
├── .godmode/
│   ├── status.json      # Current project state
│   ├── settings.json    # Per-project settings (e.g. skip-permissions)
│   ├── input.jsonl      # User input log
│   ├── output.jsonl     # Claude output log
│   ├── session-id       # Claude session ID for resumption
│   └── .gitignore       # Excludes all .godmode state from git
├── .gitignore           # Created by bootstrap scripts (excludes .godmode/)
└── (project files)      # Working directory for Claude
```

## Running the Server

### Development

```bash
dotnet run --project src/GodMode.Server/GodMode.Server.csproj
```

### Production

```bash
dotnet publish -c Release -o publish
cd publish
./GodMode.Server
```

## SignalR Hub API

### Server → Client Events

- `OutputReceived(projectId, rawJson)` — Raw Claude JSON output line
- `StatusChanged(projectId, status)` — Project status changed
- `ProjectCreated(status)` — New project created
- `CreationProgress(projectId, message)` — Script progress during project creation

### Client → Server Methods

- `Task<ProjectRootInfo[]> ListProjectRoots()` — Get roots with input schemas
- `Task<ProjectSummary[]> ListProjects()` — Get all projects
- `Task<ProjectStatus> GetStatus(projectId)` — Get project status
- `Task<ProjectDetail> CreateProject(projectRootName, inputs)` — Create project with form inputs
- `Task SendInput(projectId, input)` — Send input to Claude
- `Task StopProject(projectId)` — Stop running project
- `Task ResumeProject(projectId)` — Resume stopped project
- `Task SubscribeProject(projectId, outputOffset)` — Subscribe to output events
- `Task UnsubscribeProject(projectId)` — Unsubscribe from output
- `Task<string> GetMetricsHtml(projectId)` — Get metrics dashboard HTML

## Dependencies

- **.NET 10** — Runtime
- **SignalR** — Real-time communication
- **GodMode.Shared** — Shared types and models
- **GodMode.ProjectFiles** — Project folder management

## Troubleshooting

### Claude Process Not Starting

- Ensure `claude` command is in PATH
- Check Claude CLI is installed: `claude --version`
- Review logs for process errors

### Projects Not Recovered on Startup

- Check `ProjectRoots` configuration in appsettings.json
- Verify `status.json` files are valid JSON
- Review startup logs

### Scripts Failing

- Check script file exists with the correct extension for your OS
- Verify script has execute permissions (Linux: `chmod +x`)
- Check stderr output in the server logs
- Ensure environment variables are correct

### SignalR Connection Failures

- Verify CORS settings allow client origin
- Check firewall rules for port 31337
- Enable detailed SignalR logging
