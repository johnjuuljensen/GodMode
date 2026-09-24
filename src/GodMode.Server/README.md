# GodMode.Server

SignalR server for GodMode. It runs Claude Code sessions in project folders on the machine it runs on, streams their output to clients, and serves the React client.

## Features

- **Real-time Communication**: SignalR hub (`/hubs/projects`) for bidirectional communication
- **Process Management**: Spawn, stop and resume Claude Code processes
- **Config-Driven Project Roots**: Roots discovered from `ProjectRootsDir`, with per-action config overlays
- **Script-Based Creation**: VCS-agnostic — all prepare/create/delete logic lives in scripts, not server code
- **Cross-Platform Scripts**: Write `.ps1` scripts once; they run under `pwsh` on Windows and Linux
- **State Persistence**: Project state lives in each project's `.godmode/` folder and is recovered on restart
- **MCP Bridge**: Every session gets the `godmode-bridge` MCP server for reporting results and status back

## Configuration

### appsettings.json

```json
{
  "Authentication": { "ApiKey": "" },
  "ProjectRootsDir": "roots",
  "Urls": "http://127.0.0.1:31337"
}
```

`ProjectRootsDir` is the directory the server scans for roots: every subdirectory that contains a `.godmode-root/` folder is a root, named after the subdirectory. A relative path is resolved against the working directory. The scan runs on each call, so a root you add appears on the next refresh without a restart.

Profiles live in `{ProjectRootsDir}/.profiles/` (see `docs/UNIFIED-ARCHITECTURE.md` Section 6). An old `Profiles` or `ProjectRoots` section in `appsettings.json` is migrated there once, the first time the server starts without a `.profiles/` directory.

Every setting can also come from an environment variable (`ProjectRootsDir`, `Authentication__ApiKey`) or the command line (`--ProjectRootsDir=/srv/roots`).

### Authentication and binding

Every endpoint and the SignalR hub require authentication. Only `/health` and the React client's static files are anonymous, because the page has to load before you can enter the key. The server picks one mode at startup:

| Mode | When | Callers authenticate with |
|---|---|---|
| `codespace` | `CODESPACES=true` (set by GitHub Codespaces) | a GitHub token owned by `GITHUB_USER`. This mode wins whatever the binding and whether or not an API key is set. |
| `apikey` | `Authentication:ApiKey` is set | `Authorization: Bearer <key>` (the SignalR client sends it as `access_token` on WebSocket upgrade) |
| loopback | no key, and every binding is `127.0.0.1`, `[::1]` or `localhost` | nothing, but only from a loopback address, with a loopback `Host`, and with a loopback `Origin` when there is one (so other web pages open in your browser can't reach it) |

With no key and any other binding (`0.0.0.0`, `+`, `*`, a LAN or Tailscale address, a hostname), the server **refuses to start**. It prints why and exits with code 1.

A same-host reverse proxy or tunnel (`tailscale serve`, cloudflared, `ssh -L`, ngrok) makes remote callers look like loopback: **set a key before putting one in front of the server.**

**Docker:** the image sets `URLS=http://+:31337` (all interfaces), so a container without a key exits at startup; run it with `-e Authentication__ApiKey=<key>`. To change the binding, use the unprefixed `URLS` variable or `--urls`. `ASPNETCORE_URLS` loses to the `Urls` in `appsettings.json`.

The shipped config binds `http://127.0.0.1:31337`, so a fresh `dotnet run` is reachable only from the same machine. The browser client asks for the key once and keeps it in that browser. The MAUI app stores the key per server (the access token you enter when adding it) and adds it when relaying.

**Reaching the server from other devices.** Bind to a private-network address, such as the machine's Tailscale IP, rather than `0.0.0.0`, and set a key. Keep the loopback binding as well: projects' MCP bridge calls back to the server on `localhost`.

```bash
# Generate a key once, e.g. with: openssl rand -hex 32
export Authentication__ApiKey=<your-key>
dotnet run --project src/GodMode.Server/GodMode.Server.csproj -- \
  --urls "http://127.0.0.1:31337;http://$(tailscale ip -4):31337"
```

The key can also go in `appsettings.json` (`"Authentication": { "ApiKey": "..." }`), in user secrets, or on the command line as `--Authentication:ApiKey=<key>`.

## Project Roots

### Multi-File Config Structure

```
{ProjectRootsDir}/
└── my-root/
    ├── .godmode-root/
    │   ├── config.json               # Base/shared config (also the default action if no others exist)
    │   ├── config.freeform.json      # Freeform action (merged with config.json base)
    │   ├── config.issue.json         # Issue action (merged with config.json base)
    │   ├── freeform/                 # Freeform action resources
    │   │   ├── schema.json           # Input schema (discovered by convention)
    │   │   └── create.ps1            # Action-specific create script
    │   ├── issue/                    # Issue action resources
    │   │   ├── schema.json           # Input schema
    │   │   └── create.ps1            # Action-specific create script
    │   └── scripts/                  # Shared scripts
    │       ├── prepare.ps1           # Shared prepare script
    │       └── delete.ps1            # Shared delete script
    └── {project-id}/                 # Projects created from this root
```

`.devcontainer/godmode-server/roots/godmode-dev/` in this repository is a complete example.

### config.json — Base/Shared Config

Defines shared settings (prepare + delete scripts, environment, claude args, MCP servers) inherited by all actions:

```json
{
  "description": "Human-readable description shown in the UI",
  "profileName": "Default",
  "environment": { "KEY": "value", "CLAUDE_CONFIG_DIR": "/path/to/.claude" },
  "claudeArgs": ["--append-system-prompt", "Extra instructions"],
  "prepare": "scripts/prepare",
  "delete": "scripts/delete",
  "mcpServers": {
    "github": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-github"] }
  }
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
| Scalars (description, nameTemplate, model, etc.) | Overlay replaces if present |
| `environment` | Dictionary merge, overlay keys override |
| `mcpServers` | Dictionary merge, overlay servers override by name |
| `claudeArgs` | Concatenated (base + overlay) |
| Script fields (prepare, create, delete) | Overlay replaces entirely |

`profileName` and `stripEnvVarProfile` are read from `config.json` only.

### Action Discovery

- Scan `config.*.json` → action names from filenames
- If only `config.json` exists (no `config.*.json`) → single default "Create" action
- If no config exists at all → default form with name + prompt fields

### Fields

| Field | Description |
|-------|-------------|
| `description` | Shown in the UI when selecting an action |
| `profileName` | Profile the root belongs to (`config.json` only). Default: `Default` |
| `environment` | Env vars set for scripts and passed to Claude processes. Values support `${VAR}` expansion from the server's environment |
| `prepare` | Scripts run before project folder is created (working dir = root) |
| `create` | Scripts run to create the project (working dir = project, or root if `scriptsCreateFolder`) |
| `delete` | Scripts run when a project is deleted (working dir = root) |
| `claudeArgs` | Extra CLI arguments appended when starting Claude |
| `model` | Default `--model` for the action. A `model` form input overrides it |
| `mcpServers` | MCP servers for the root or action, merged over the profile's (profile → root → action) |
| `nameTemplate` | Derive project name from inputs, e.g. `"issue_{issueNumber}"` |
| `promptTemplate` | Derive initial prompt from inputs |
| `scriptsCreateFolder` | If true, create scripts are responsible for creating the project directory |
| `stripEnvVarProfile` | If true (`config.json` only), server env vars prefixed with the profile name reach sessions without the prefix: `MEGA_GITHUB_TOKEN` → `GITHUB_TOKEN` for profile `mega` |

Script fields accept either a single string or a string array in JSON. Paths are relative to `.godmode-root/`.

### Input Schema (Convention-Based)

Place a `schema.json` file in the action's folder: `{actionName}/schema.json`. If no schema file exists, the default schema (name + prompt + skip permissions) is used.

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

Some keys have special meaning: `name` and `prompt` are the project name and initial Claude prompt unless `nameTemplate`/`promptTemplate` override them, `skipPermissions` starts Claude with `--dangerously-skip-permissions`, and `model` overrides the action's model.

### Scripts

Scripts are the abstraction layer for all VCS and setup operations. The server doesn't know about git, mercurial, or any other tool.

**Cross-platform**: Specify scripts without extension in the config. The server resolves to the right file based on OS:
- Windows: tries `.ps1`, `.cmd`, `.bat`
- Linux/Mac: tries `.sh`, then `.ps1`

`.ps1` runs under `pwsh`, `.sh` under `bash`, `.cmd`/`.bat` under `cmd`. A single `.ps1` therefore works everywhere; see the script constraints in `CLAUDE.md`. A script named with an explicit extension in the config (for example `"prepare": "scripts/prepare.ps1"`) is used as-is.

**Environment variables** available to all scripts:

| Variable | Description |
|----------|-------------|
| `GODMODE_ROOT_PATH` | Root directory path |
| `GODMODE_PROJECT_PATH` | Project directory path |
| `GODMODE_PROJECT_ID` | The project's folder name (not the project ID below; claude's own `GODMODE_PROJECT_ID`, for the MCP bridge, is the project ID) |
| `GODMODE_PROJECT_NAME` | Display name |
| `GODMODE_INPUT_*` | All form inputs (key in upper snake case, e.g. `GODMODE_INPUT_ISSUE_NUMBER`) |
| `GODMODE_RESULT_FILE` | Create scripts only: a file the script can write `key=value` lines to (see below) |
| `GODMODE_FORCE` | Delete scripts only: `true` when the user forced the delete |
| *(from `environment`)* | All vars from the profile's `env.json` and the config's `environment` block |

A create script can override the project's `project_path`, `project_name` or `project_prompt` by writing them to `GODMODE_RESULT_FILE`, one `key=value` per line. The last key may span several lines, which suits a multiline prompt.

Script stdout is streamed to the client as creation progress. Non-zero exit code aborts creation.

## Project Folder Structure

Each project is stored in a folder under its root:

```
{root}/{folder}/
├── .godmode/
│   ├── status.json      # Current project state
│   ├── settings.json    # Per-project settings (e.g. skip-permissions)
│   ├── input.jsonl      # User input log
│   ├── output.jsonl     # Claude output log
│   ├── session-id       # Claude session ID for resumption
│   └── .gitignore       # Excludes all .godmode state from git
└── (project files)      # Working directory for Claude
```

Archived projects move to `{root}/.archived/{folder}/`.

**Project ID.** A project is identified by `{profile}/{root}/{folder}`: where its folder is. Two projects with the same name in different roots or profiles are separate projects, with their own process, output and SignalR group. Clients treat the ID as opaque and pass it back as they received it. The server derives it from the folder's location on every start and writes it to `status.json`, so a folder from before this format (its `Id` the bare folder name), or one whose root has moved to another profile, is recovered under its current ID. Nothing else in `.godmode` holds the ID.

The folder name comes from the project's name: spaces become underscores and characters that are invalid in a file name are dropped. A name that leaves no folder of its own (empty, `.`, `..`, or dots only) is refused before anything is created or run. So is a create script's `project_path` at or above the root.

## Running the Server

### Development

```bash
dotnet run --project src/GodMode.Server/GodMode.Server.csproj
```

The build runs `npm run build` in `src/GodMode.Client.React` when the client's sources changed, and the server serves the result from `wwwroot/`. Run `npm ci` there once first. `npm run dev` cannot reach the server: `vite.config.ts` has no proxy.

### Production

```bash
dotnet publish src/GodMode.Server/GodMode.Server.csproj -c Release -o publish
cd publish
./GodMode.Server
```

The machine also needs `claude` on the `PATH`, `pwsh` for root scripts, and Node for the MCP bridge (`npm ci && npm run build` in `src/GodMode.McpBridge`, or point `GODMODE_MCP_BRIDGE_PATH` at its `dist/index.js`).

## SignalR Hub API

### Client → Server Methods

Projects:
- `Task<ProjectSummary[]> ListProjects()` — Get all projects
- `Task<ProjectStatus> GetStatus(projectId)` — Get project status
- `Task<ProjectStatus> CreateProject(profileName, projectRootName, actionName, inputs)` — Create a project with form inputs (`actionName` null = default action)
- `Task SendInput(projectId, input)` — Send input to Claude
- `Task StopProject(projectId)` — Stop running project
- `Task ResumeProject(projectId)` — Resume stopped project
- `Task SubscribeProject(projectId, outputOffset)` — Subscribe to output events
- `Task UnsubscribeProject(projectId)` — Unsubscribe from output
- `Task DeleteProject(projectId, force)` — Run delete scripts and remove the project
- `Task ArchiveProject(projectId)` / `Task UnarchiveProject(projectId)` — Move to and from `.archived/`
- `Task<ProjectSummary[]> ListArchivedProjects()` — Get archived projects

Roots and profiles:
- `Task<ProjectRootInfo[]> ListProjectRoots()` — Get roots with their actions and input schemas
- `Task<ProfileInfo[]> ListProfiles()` — Get profiles
- `Task CreateProfile(name, description)` / `Task DeleteProfile(name, deleteContents)` / `Task UpdateProfileDescription(name, description)` — Edit `.profiles/`

Utility:
- `Task<string?> CheckCommand(command)` — Resolve a command on the server's `PATH`

### Server → Client Events

- `OutputReceived(projectId, rawJson)` — Raw Claude JSON output line
- `StatusChanged(projectId, status)` — Project status changed
- `ProjectCreated(status)` — New project created
- `CreationProgress(projectId, message)` — Script progress during project creation
- `ProjectDeleted(projectId)`, `ProjectArchived(projectId)`, `ProjectRestored(project)` — Project list changes
- `ProfilesChanged()` — Profiles changed; refresh the list

### HTTP Endpoints

- `GET /health` — Anonymous liveness probe
- `GET /servers`, `GET /events` — The same shape as the MAUI app's local proxy, so the React client works against either
- `POST /api/internal/result`, `/status`, `/review` — Called by the MCP bridge with its per-project token

## Dependencies

- **.NET 10** — Runtime
- **SignalR** — Real-time communication
- **GodMode.Shared** — Shared types and models
- **GodMode.ProjectFiles** — Project folder management

## Troubleshooting

### Server Exits at Startup

- "will not start: no API key is configured": you bound a non-loopback address without a key. See *Authentication and binding* above.

### Claude Process Not Starting

- Ensure `claude` command is in PATH
- Check Claude CLI is installed: `claude --version`
- Review logs in `.godmode-logs/` under the working directory

### Roots or Projects Missing

- Check `ProjectRootsDir` points where you think (relative paths resolve against the working directory)
- Each root needs a `.godmode-root/` folder directly inside it
- Verify `status.json` files are valid JSON
- Review startup logs

### Scripts Failing

- Check a script with a matching extension exists for your OS (a `.ps1` needs `pwsh` on the `PATH`)
- Check stderr output in the server logs
- Ensure environment variables are correct

### SignalR Connection Failures

- The browser client asks for the API key when the server requires one; a rejected key shows the key page again
- Check firewall rules for port 31337
