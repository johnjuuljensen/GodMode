# GodMode.Server

SignalR server for the Claude Autonomous Development System. This lightweight .NET server manages Claude Code processes and provides real-time communication with clients.

## Features

- **Real-time Communication**: SignalR hub for bidirectional communication
- **Process Management**: Spawn and control Claude Code processes
- **Config-Driven Project Roots**: Multi-file config discovery with per-action overlays
- **Script-Based Creation**: VCS-agnostic — all prepare/create/delete logic lives in scripts, not server code
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
  "Urls": "http://0.0.0.0:31337"
}
```

`ProjectRoots` maps logical names to directory paths. Each root can optionally contain a `.godmode-root/` directory with config files to customize the creation workflow. Roots without config get a default form (project name + prompt).

## Project Roots

### Multi-File Config Structure

```
.godmode-root/
├── config.json               # Base/shared config (also the default action if no others exist)
├── config.freeform.json      # Freeform action (merged with config.json base)
├── config.issue.json         # Issue action (merged with config.json base)
├── freeform/                 # Freeform action resources
│   ├── schema.json           # Input schema (discovered by convention)
│   └── create.ps1 / .sh      # Action-specific create script
├── issue/                    # Issue action resources
│   ├── schema.json           # Input schema
│   └── create.ps1 / .sh      # Action-specific create script
└── scripts/                  # Shared scripts
    ├── prepare.ps1 / .sh     # Shared prepare script
    └── delete.ps1 / .sh      # Shared delete script
```

### config.json — Base/Shared Config

Defines shared settings (prepare + delete scripts, environment, claude args) inherited by all actions:

```json
{
  "description": "Human-readable description shown in the UI",
  "environment": { "KEY": "value", "CLAUDE_CONFIG_DIR": "/path/to/.claude" },
  "claudeArgs": ["--append-system-prompt", "Extra instructions"],
  "prepare": "scripts/prepare",
  "delete": "scripts/delete"
}
```

### config.{action}.json — Per-Action Overlay

Each `config.*.json` file defines an action. Action name is derived from the filename. Fields are merged with config.json:

```json
{
  "description": "Create project from a GitHub issue",
  "scriptsCreateFolder": true,
  "create": "issue/create",
  "nameTemplate": "issue_{issueNumber}",
  "promptTemplate": "Read GitHub issue #{issueNumber}..."
}
```

### Config Merging Rules

When resolving an action, `config.json` (base) is merged with `config.{action}.json` (overlay):

| Field | Merge Rule |
|-------|-----------|
| Scalars (description, nameTemplate, etc.) | Overlay replaces if present |
| `environment` | Dictionary merge, overlay keys override |
| `claudeArgs` | Concatenated (base + overlay) |
| Script fields (prepare, create, delete) | Overlay replaces entirely |

### Action Discovery

- Scan `config.*.json` → action names from filenames
- If only `config.json` exists (no `config.*.json`) → single default "Create" action
- If no config exists at all → default form with name + prompt fields

### Fields

| Field | Description |
|-------|-------------|
| `description` | Shown in the UI when selecting an action |
| `environment` | Env vars set for scripts and passed to Claude processes |
| `prepare` | Scripts run before project folder is created (working dir = root) |
| `create` | Scripts run to create the project (working dir = project or root if scriptsCreateFolder) |
| `delete` | Scripts run when a project is deleted |
| `claudeArgs` | Extra CLI arguments appended when starting Claude |
| `nameTemplate` | Derive project name from inputs, e.g. `"issue_{issueNumber}"` |
| `promptTemplate` | Derive initial prompt from inputs |
| `scriptsCreateFolder` | If true, create scripts are responsible for creating the project directory |

Script fields accept either a single string or a string array in JSON. Paths are relative to `.godmode-root/`.

### Input Schema (Convention-Based)

Place a `schema.json` file in the action's folder: `{actionName}/schema.json`. If no schema file exists, the default schema (name + prompt) is used.

Supported JSON Schema types:
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
    "skipPermissions": { "type": "boolean", "title": "Skip Permissions", "default": "true" }
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

If a script is specified with an explicit extension (e.g. `"scripts/setup.ps1"`), it's used as-is.

**Environment variables** available to all scripts:

| Variable | Description |
|----------|-------------|
| `GODMODE_ROOT_PATH` | Root directory path |
| `GODMODE_PROJECT_PATH` | Project directory path |
| `GODMODE_PROJECT_ID` | Folder name / project ID |
| `GODMODE_PROJECT_NAME` | Display name |
| `GODMODE_INPUT_*` | All form inputs (key uppercased, e.g. `GODMODE_INPUT_ISSUE_NUMBER`) |
| *(from `environment`)* | All vars from the config's `environment` block |

Script stdout is streamed to the client as creation progress. Non-zero exit code aborts creation.

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
├── .gitignore           # Created by create scripts (excludes .godmode/)
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
