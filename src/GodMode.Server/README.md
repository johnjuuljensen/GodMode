# GodMode.Server

SignalR server for GodMode. It runs Claude Code sessions in project folders on the machine it runs on, streams their output to clients, and serves the React client.

## Features

- **Real-time Communication**: SignalR hub (`/hubs/projects`) for bidirectional communication
- **Process Management**: Spawn, stop and resume Claude Code processes
- **Config-Driven Project Roots**: Roots discovered from `ProjectRootsDir`, with per-action config overlays
- **Script-Based Creation**: VCS-agnostic — all prepare/create/delete logic lives in scripts, not server code
- **Cross-Platform Scripts**: Write `.ps1` scripts once; they run under `pwsh` on Windows and Linux
- **State Persistence**: Project state lives in each project's `.godmode/` folder and is recovered on restart
- **Permission prompts**: Every session asks the user for permission through the server's own MCP endpoint, `/mcp`, whose one tool is claude's `--permission-prompt-tool`

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

Profiles live in `{ProjectRootsDir}/.profiles/` (see `docs/UNIFIED-ARCHITECTURE.md` Section 6). Profiles and roots are maintained by hand on the host: the server reads their config and never writes it.

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

**Reaching the server from other devices.** Bind to a private-network address, such as the machine's Tailscale IP, rather than `0.0.0.0`, and set a key. Keep the loopback binding as well: projects' claude calls the server's MCP endpoint on it.

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
    │       ├── delete.ps1            # Shared delete script
    │       └── status.ps1            # Reports the project's pull request (optional)
    └── {project-id}/                 # Projects created from this root
```

`.devcontainer/godmode-server/roots/godmode-dev/` in this repository is a complete example.

### config.json — Base/Shared Config

Defines shared settings (prepare + delete scripts, environment, claude args) inherited by all actions:

```json
{
  "description": "Human-readable description shown in the UI",
  "profileName": "Default",
  "environment": { "KEY": "value", "CLAUDE_CONFIG_DIR": "/path/to/.claude" },
  "claudeArgs": ["--append-system-prompt", "Extra instructions"],
  "prepare": "scripts/prepare",
  "delete": "scripts/delete",
  "status": "scripts/status",
  "resumeOnRestart": true,
  "resumePrompt": "The GodMode server restarted and interrupted you. Continue where you left off."
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
| Scalars (description, nameTemplate, model, resumeOnRestart, resumePrompt, etc.) | Overlay replaces if present |
| `environment` | Dictionary merge, overlay keys override |
| `claudeArgs` | Concatenated (base + overlay) |
| Script fields (prepare, create, delete, status) | Overlay replaces entirely |

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
| `status` | One script that reports the project's pull request (working dir = project): see [Pull request status](#pull-request-status) |
| `claudeArgs` | Extra CLI arguments appended when starting Claude |
| `model` | Default `--model` for the action. A `model` form input overrides it |
| `nameTemplate` | Derive project name from inputs, e.g. `"issue_{issueNumber}"` |
| `promptTemplate` | Derive initial prompt from inputs |
| `scriptsCreateFolder` | If true, create scripts are responsible for creating the project directory |
| `resumeOnRestart` | Whether a project that was active when the server stopped carries on when it starts again. Default `true`: see [Resuming after a restart](#resuming-after-a-restart) |
| `resumePrompt` | What a project that was working when the server stopped is told when it is resumed. Default `"The GodMode server restarted and interrupted you. Continue where you left off."` |
| `stripEnvVarProfile` | If true (`config.json` only), server env vars prefixed with the profile name reach sessions without the prefix: `MEGA_GITHUB_TOKEN` → `GITHUB_TOKEN` for profile `mega` |

Script fields accept either a single string or a string array in JSON. Paths are relative to `.godmode-root/`.

### MCP Servers

GodMode gives a session one MCP server, its own: `godmode`, this server's `/mcp` endpoint, in the `--mcp-config` file it launches claude with (see [The MCP endpoint](#the-mcp-endpoint)). It configures no others:

- A repo brings its MCP servers in its own `.mcp.json` (Claude Code's project scope).
- User-scoped servers live in the profile's Claude config: the `CLAUDE_CONFIG_DIR` its `environment` (or the root's) sets, for example with `claude mcp add --scope user` run with that `CLAUDE_CONFIG_DIR`.

A root or action config that still has `mcpServers`, or a profile with an `mcp/` folder, launches normally: the server logs a warning once for each, and ignores it.

GodMode pre-approves no tool: it passes no `--allowedTools`. A tool call that needs approval, an MCP tool's included, reaches the permission prompt (`WaitingPermission`), unless Claude Code's own settings allow it (`permissions.allow` in the profile's `CLAUDE_CONFIG_DIR`, or the repo's `.claude/settings.json`) or the project runs with `skipPermissions`.

### The MCP endpoint

`/mcp` serves MCP over streamable HTTP, statelessly, with one tool: `permission_prompt`. claude is launched with `--permission-prompts host --permission-prompt-tool mcp__godmode__permission_prompt` and an MCP config whose only entry is:

```json
{ "mcpServers": { "godmode": {
  "type": "http",
  "url": "http://127.0.0.1:31337/mcp",
  "headers": { "Authorization": "Bearer <project token>", "X-GodMode-Project-Id": "<project ID>" }
} } }
```

- **The URL** is an address this machine reaches the server on, from the addresses it is bound to: a loopback binding first, a wildcard's `127.0.0.1` next, else the one IP bound.
- **The token** is issued afresh for each launch and lives only in memory and in this file. The file is `.godmode/mcp-config.json` in the project, owner-only where the OS allows, and is deleted when the process exits. No environment variable carries it.
- **Only a project token opens `/mcp`**, and only for the project it was issued to. Neither the user's API key nor a keyless loopback caller gets in, and a project token opens nothing else.
- **The tool** takes claude's flat arguments, `tool_name`, `input` (an object) and `tool_use_id` (optional). It waits until the user answers, however long that takes, and returns claude `{"behavior":"allow","updatedInput":{…}}` or `{"behavior":"deny","message":"…"}` as text.
- **While it waits,** it sends a progress notification every `PermissionPromptKeepAliveSeconds` (default 30). claude gives up on a tool call that sends no response or progress for 300 seconds.
- **When claude cancels the call**, or its connection drops, the request is withdrawn (denied).

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
    "skipPermissions": { "type": "boolean", "title": "Skip Permissions", "default": false }
  },
  "required": ["name", "prompt"]
}
```

Some keys have special meaning: `name` and `prompt` are the project name and initial Claude prompt unless `nameTemplate`/`promptTemplate` override them, `skipPermissions` starts Claude with `--dangerously-skip-permissions` (without it, a tool call that needs approval waits for the user: `WaitingPermission`), and `model` overrides the action's model.

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
| `GODMODE_PROJECT_ID` | The project's folder name (not the project ID below) |
| `GODMODE_PROJECT_NAME` | Display name |
| `GODMODE_INPUT_*` | All form inputs (key in upper snake case, e.g. `GODMODE_INPUT_ISSUE_NUMBER`) |
| `GODMODE_RESULT_FILE` | Create scripts only: a file the script can write `key=value` lines to (see below) |
| `GODMODE_FORCE` | Delete scripts only: `true` when the user forced the delete |
| *(from `environment`)* | All vars from the profile's `env.json` and the config's `environment` block |

A create script can override the project's `project_path`, `project_name` or `project_prompt` by writing them to `GODMODE_RESULT_FILE`, one `key=value` per line. The last key may span several lines, which suits a multiline prompt.

Script stdout is streamed to the client as creation progress. Non-zero exit code aborts creation.

### Pull Request Status

A root's `status` script tells the server what became of a project's work, without the server knowing the VCS. It runs in the project folder, with the environment above, and prints one JSON object:

```json
{"pullRequest": {"url": "https://github.com/o/r/pull/12", "number": 12, "state": "draft|open|merged|closed", "review": "none|changes_requested|approved"}}
```

or `{}` when there is no pull request. The result is `ProjectStatus.PullRequest` (in `status.json`), with `ChangedAt`, when the server first saw that state and review.

- **When:** on every transition to Idle or Stopped, and every `PullRequestPollSeconds` (default 600) while the pull request is draft or open; for an open one also when the server starts. One check at a time per project, at most four across the server. Deleting a project stops its checks.
- **Failures change nothing:** a non-zero exit, more than `StatusScriptTimeoutSeconds` (default 30, then the script is killed), more than 16 KB of stdout, or output that is not exactly the object above (unknown properties, other values, extra text) leaves the pull request as it was, and is logged as a warning, on every check that fails.
- **Attention:** an open pull request with `changes_requested`, on a project that is Idle or Stopped, is a `Review` item until `MarkSeen` or a reply, and again when the review changes. `Review` and `Finished` items carry `PullRequestUrl`.
- A root without `status` runs nothing, and its projects have no `PullRequest`.

`godmode-dev`'s `scripts/status.ps1` is an example using `gh pr view`. It prints `{}` only when there is no pull request (none for the branch, not a repository, a detached HEAD, `git` or `gh` not installed); any other `gh` failure, such as a network error, a rate limit or an expired login, exits non-zero, so the server keeps what it knew and keeps polling.

### Resuming After a Restart

When the server stops, it stops every project, and one that was `Running`, `WaitingInput` or `WaitingPermission` keeps that in `ProjectStatus.StateAtShutdown` (in `status.json`) beside `Stopped`. When it starts again, once it is listening and has recovered the projects, it carries on with them as the project's action says:

- **Working** (`Running`, or `WaitingPermission`, whose prompt the shutdown denied): resumed with `--resume <session-id>`, launched as any resume is, and sent `resumePrompt` as its first input. Three are resumed at a time, each until claude reports `system/init`.
- **Waiting on a question** (`WaitingInput`): no process is launched. The project is `WaitingInput` again with its `CurrentQuestion`, and still a `Question` in `GetAttention`, until a reply (`ReplyAndResume`) resumes it with the answer. An AskUserQuestion's options do not survive: the shutdown denied it, and what is left is its first question's text, answered in the chat.
- **`resumeOnRestart: false`**: the project stays `Stopped`.

A project the user stopped, or that was `Idle`, `Stopped` or `Error` when the server stopped, is not resumed. A resume that fails is `Error` with `LastError`, as any resume is (a root config the launch cannot use, a claude that exits at once), and is not tried again. Any launch clears `StateAtShutdown`, and so does a stop by the user. A project waiting its turn is decided when it comes: one the user has stopped, resumed or answered meanwhile is left as it is. A shutdown while the start is still resuming launches nothing more, and the projects not resumed yet keep their marker for the next start. The marker is only a field in `status.json`: a `status.json` restored from a backup or copied from another machine carries it, and that project is resumed on the next start.

A Ctrl+C on a server run in a terminal reaches claude too, and claude may exit before the server's shutdown begins. An exit on its own no more than `ExitBeforeShutdownWindowSeconds` (default 5) before the shutdown, with nothing changed since, counts as stopped by it: the project keeps its question and is resumed like the rest. A server that is killed (no shutdown runs) leaves no `StateAtShutdown`, and its projects are recovered `Stopped`.

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

A project is a folder directly inside its root that has a `.godmode/status.json`. Nothing deeper is recovered, and the server moves no project folder anywhere.

**Project ID.** A project is identified by `{profile}/{root}/{folder}`: where its folder is. Two projects with the same name in different roots or profiles are separate projects, with their own process, output and SignalR group. Clients treat the ID as opaque and pass it back as they received it. The server derives it from the folder's location on every start and writes it to `status.json`, so a folder that was moved, or whose root has moved to another profile, is recovered under its current ID. Nothing else in `.godmode` holds the ID.

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

The machine also needs `claude` on the `PATH` and `pwsh` for root scripts. The server itself needs no Node. A repo whose `.mcp.json` starts MCP servers with `npx` needs it, as does one with a JavaScript toolchain.

## SignalR Hub API

### Client → Server Methods

Projects:
- `Task<ProjectSummary[]> ListProjects()` — Get all projects
- `Task<ProjectStatus> GetStatus(projectId)` — Get project status
- `Task<ProjectStatus> CreateProject(profileName, projectRootName, actionName, inputs)` — Create a project with form inputs (`actionName` null = default action)
- `Task SendInput(projectId, input)` — Send input to Claude (while a permission prompt or question waits, it answers that instead)
- `Task RespondToPermission(projectId, requestId, decision)` — Allow or deny the project's `PendingPermission`
- `Task AnswerQuestion(projectId, requestId, answers)` — Answer the project's `PendingQuestion` (question text → chosen label or free text)
- `Task StopProject(projectId)` — Stop running project
- `Task ResumeProject(projectId)` — Resume stopped project
- `Task SubscribeProject(projectId, fromOffset)` — Replay `output.jsonl` from `fromOffset` (the byte offset after the last line the client has; 0 for all, `-N` for the last N turns) in `OutputBatch` messages, then `OutputReplayComplete`, then live `OutputReceived` lines, each line once and in order
- `Task UnsubscribeProject(projectId)` — Unsubscribe from output
- `Task DeleteProject(projectId, force)` — Run delete scripts and remove the project

Attention:
- `Task<AttentionItem[]> GetAttention()` — Every project that needs the user (`Permission`, `Question`, `Error`, `Review`, `Finished`), oldest first, with a short plain `Text`; the same after a restart
- `Task MarkSeen(projectId)` — The last result is seen: no longer `Finished`, nor `Review` until the pull request changes (a reply does the same)
- `Task ReplyAndResume(projectId, text)` — `SendInput` to a running claude; otherwise resume, send, and return once claude reports `system/init` (fails on exit or after `SessionStartTimeoutSeconds`, default 60)

Roots and profiles:
- `Task<ProjectRootInfo[]> ListProjectRoots()` — Get roots with their actions and input schemas
- `Task<ProfileInfo[]> ListProfiles()` — Get profiles (read-only: no hub method writes a profile or a root)

Utility:
- `Task<string?> CheckCommand(command)` — Resolve a command on the server's `PATH`

### Server → Client Events

- `OutputReceived(projectId, offset, rawJson)` — A live raw Claude JSON output line; `offset` is the byte offset in `output.jsonl` just after it
- `OutputBatch(projectId, fromOffset, lines)` — Replayed `OutputLine`s (`Offset`, `RawJson`) covering `output.jsonl` from `fromOffset`; a replay from 0 when more was asked for means the client's transcript is not from this file
- `OutputReplayComplete(projectId, offset)` — The subscription's replay is done at `offset`; live lines follow
- `StatusChanged(projectId, status)` — Project status changed
- `AttentionChanged(items)` — The whole `GetAttention` list, pushed only when it differs from the last one pushed
- `ProjectCreated(status)` — New project created
- `CreationProgress(projectId, message)` — Script progress during project creation
- `ProjectDeleted(projectId)` — Project deleted

### HTTP Endpoints

- `GET /health` — Anonymous liveness probe
- `GET /servers`, `GET /events` — The same shape as the MAUI app's local proxy, so the React client works against either
- `POST /mcp` — GodMode's MCP endpoint, for its sessions' claude, with the project token of its MCP config: see [The MCP endpoint](#the-mcp-endpoint)

## Dependencies

- **.NET 10** — Runtime
- **SignalR** — Real-time communication
- **ModelContextProtocol.AspNetCore** — The MCP endpoint
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
