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

Every endpoint and the SignalR hub require authentication, whatever the server is bound to, loopback included: nothing on this machine gets in without the key either. Only `/health` and the React client's static files are anonymous, because the page has to load before you can enter the key. The server picks one mode at startup:

| Mode | When | Callers authenticate with |
|---|---|---|
| `codespace` | `CODESPACES=true` (set by GitHub Codespaces) | a GitHub token owned by `GITHUB_USER`, other than the codespace's own `GITHUB_TOKEN`: its sessions are given that one, so the server refuses it. This mode wins whatever the binding and whether or not an API key is set. |
| `apikey` | anywhere else | `Authorization: Bearer <key>` (the SignalR client sends it as `access_token` on the WebSocket upgrade) |

**The key** is `Authentication:ApiKey` when that is set. Otherwise the server generates a 256-bit key on its first start, prints it once to the console with how to use it, and keeps it in its key file, which it reads on every later start. A restart keeps the key.

| The server runs on | Its key file |
|---|---|
| Windows | `%LOCALAPPDATA%\GodMode.Server\api-key` |
| Linux | `$XDG_DATA_HOME/GodMode.Server/api-key`, by default `~/.local/share/GodMode.Server/api-key` |
| macOS | `~/Library/Application Support/GodMode.Server/api-key` |
| The Docker image | `/home/godmode/.local/share/GodMode.Server/api-key`, in the home of the image's non-root `godmode` user, who owns it |

- **Owner-only.** The file is created readable by the server's user alone: mode 0600 in a 0700 directory on Linux and macOS, an ACL of that user alone on Windows. It is created with those permissions rather than changed afterwards, so a file system that refuses permission changes (Azure Files, other network mounts) does not stop the server; there it is a plain file.
- **Another place:** `Authentication:ApiKeyFile`. It must not be under `ProjectRootsDir`, where sessions work: the server refuses to start if it is.
- **Read it again** with `cat ~/.local/share/GodMode.Server/api-key` (Windows: `type %LOCALAPPDATA%\GodMode.Server\api-key`). Write your own key into it, or delete it for a new one on the next start.
- **A configured key always wins**, and the key file is then neither read nor written. So does a codespace, which uses no key.
- **Docker:** a replaced container has a new home, so a new key. Run it with `-e Authentication__ApiKey=<key>`, or keep the key file on a named volume: `-v godmode-key:/home/godmode/.local/share/GodMode.Server`. The image creates that directory, owned by `godmode` with mode 0700, and a new named volume starts with its owner and mode. A bind mount (`-v /srv/godmode-key:…`) keeps the host directory's owner instead, which must be writable by the container's `godmode` user.

A key of your own can go in `appsettings.json` (`"Authentication": { "ApiKey": "..." }`), in user secrets, in the `Authentication__ApiKey` environment variable, or on the command line as `--Authentication:ApiKey=<key>`. `openssl rand -hex 32` makes one.

**Browser origins.** A browser sends an `Origin` header on every WebSocket upgrade, which CORS does not cover, and on any request but a same-origin GET. A request with an `Origin` is let through only from one of the server's own origins, and is refused with 403, before authentication, from anywhere else, `Origin: null` included. The server's own origins are:

- the scheme, host and port of each address it listens on. A loopback address, or a wildcard (`0.0.0.0`, `+`, `*`, `[::]`, which listens on loopback too), also stands for `localhost`, `127.0.0.1` and `[::1]` on its port;
- the origins listed in `Authentication:AllowedOrigins`: addresses you reach the server by that it cannot tell are its own, such as a reverse proxy's (`https://machine.tailnet.ts.net`) or a host name on a wildcard binding (`http://nas.local:31337`). It is a list (`Authentication__AllowedOrigins__0=https://…`) or one `;`-separated string, and an entry that is not an origin stops the server at startup;
- in a codespace, its forwarded port's: `https://<codespace-name>-31337.app.github.dev`;
- in Development, the Vite dev server's, `http://localhost:5173`.

So a page served from another port on this machine, such as a dev server a session starts, cannot use the server, with the key or without. A request with no `Origin` (the MAUI app's relay and attention service, `curl`) needs the key alone. The server logs the origins it accepts when it starts, and a warning for each request it refuses, naming the origin.

**Bindings.** The shipped config binds `http://127.0.0.1:31337`, so a fresh `dotnet run` is reachable only from the same machine, and still needs the key. The browser client asks for the key once and keeps it in that browser. The MAUI app stores the key per server (the API key you enter when adding it) and adds it when relaying.

**Reaching the server from other devices.** Bind to a private-network address, such as the machine's Tailscale IP, rather than `0.0.0.0`. Keep the loopback binding as well: projects' claude calls the server's MCP endpoint on it. A phone's browser that opens `http://<tailscale-ip>:31337` is on one of the server's own origins; one that opens it by a name (MagicDNS, `tailscale serve`) needs that origin in `Authentication:AllowedOrigins`.

```bash
dotnet run --project src/GodMode.Server/GodMode.Server.csproj -- \
  --urls "http://127.0.0.1:31337;http://$(tailscale ip -4):31337"
```

**Docker:** the image sets `URLS=http://+:31337` (all interfaces). A browser on the Docker host at `http://localhost:31337`, with the port published as the same number, is on the server's own origin. Any other address you open it by (a host name, a LAN address, another published port such as `-p 8080:31337`) goes in `Authentication:AllowedOrigins`, for example `-e Authentication__AllowedOrigins__0=http://nas.local:8080`. To change the binding, use the unprefixed `URLS` variable or `--urls`. `ASPNETCORE_URLS` loses to the `Urls` in `appsettings.json`.

**What the key does not stop.** Sessions run as the server's own OS user. A session that can run arbitrary commands can read the key file, `appsettings.json` or the server's environment, and with the key drive the hub, answering its own permission prompts. The permission prompt is a gate as long as the commands it approves don't do that; it is not a sandbox. The server hands neither a session nor a root script the key (their environment is an allowlist, see [Environment](#environment), and the key file is never under `ProjectRootsDir`), but real isolation, a separate OS user or container per session, is out of scope. The same goes for a codespace's `GITHUB_TOKEN`: the server refuses it, but sessions that are given it hold it.

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
  "permissionMode": "auto",
  "allowSkipPermissions": false,
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
| Scalars (description, nameTemplate, model, permissionMode, allowSkipPermissions, resumeOnRestart, resumePrompt, etc.) | Overlay replaces if present |
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
| `environment` | Env vars set for scripts and passed to Claude processes, on top of the few they inherit (see [Environment](#environment)). Values support `${VAR}` expansion from the server's environment |
| `prepare` | Scripts run before project folder is created (working dir = root) |
| `create` | Scripts run to create the project (working dir = project, or root if `scriptsCreateFolder`) |
| `delete` | Scripts run when a project is deleted (working dir = root) |
| `status` | One script that reports the project's pull request (working dir = project): see [Pull request status](#pull-request-status) |
| `claudeArgs` | Extra CLI arguments appended when starting Claude |
| `model` | Default `--model` for the action. A `model` form input overrides it |
| `permissionMode` | claude's `--permission-mode` for the action's projects: `acceptEdits`, `auto`, `manual`, `dontAsk` or `plan`. Kept with each project at create. Default: none (claude's own settings decide). See [Permissions](#permissions) |
| `allowSkipPermissions` | Whether the action's projects may run with `--dangerously-skip-permissions`. Default `false`. See [Permissions](#permissions) |
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

GodMode pre-approves no tool: it passes no `--allowedTools`. A tool call that needs approval, an MCP tool's included, reaches the permission prompt (`WaitingPermission`), unless Claude Code's own settings allow it (`permissions.allow` in the profile's `CLAUDE_CONFIG_DIR`, or the repo's `.claude/settings.json`), the root's `permissionMode` lets it through, or the project runs with skip-permissions (see [Permissions](#permissions)).

### Permissions

A root decides how its sessions are permitted, with two keys in `config.json` or an action's overlay (the overlay wins).

**`permissionMode`** is the normal way to let a session work unattended. It is passed as `--permission-mode <mode>`, beside the permission prompt, which stays: what the mode does not decide still reaches the user.

- **Values:** `acceptEdits`, `auto`, `manual`, `dontAsk` and `plan`, in any case (passed as claude spells them). `bypassPermissions` is refused: skipping is `allowSkipPermissions`' to allow. Any other value is a config error in the file that has it: a create fails, naming the file and the value, before anything is written; the roots listing leaves that action out (or, in `config.json`, lists the root as the default config); a resume fails as it does for any config it cannot read.
- **Kept with the project.** A create stores the action's mode in the project's `.godmode/settings.json` (`permissionMode`), and every launch of it uses that one, even after the root's config has changed. A project with none stored, such as one created before the root had a mode, takes the root's current one. The stored one is checked again at each launch, since the session can write that file: an unknown one, or `bypassPermissions`, is left out, with a warning.
- **Skip-permissions overrides it:** a project that runs with skip is launched without `--permission-mode`, with a warning.
- **Measured with claude 2.1.282 on Windows**, for six requests (Bash `echo hello > probe.txt`, a Write, an Edit, a WebFetch, Bash `curl -s -o page.html …`, Bash `rm -f …` in the project), each answered Allow where it was asked:
  - `manual` (or no mode): all six reached the permission prompt.
  - `acceptEdits`: the WebFetch and the `curl` did; the rest ran.
  - `auto`: none did. Its classifier let all six run.
  - `dontAsk`: none did. All but the Edit were denied, and the Edit failed on the file the denied Write had not made.
  - `auto` with `--model haiku`: claude ran in its default mode (its `system/init` said `permissionMode: default`), and all six reached the prompt. Nothing says so but that line.

**`allowSkipPermissions`** (default `false`) is the only way a session runs with `--dangerously-skip-permissions`, which nothing then asks about.

- **Where it is false,** the create form does not offer the schema's `skipPermissions`, and a create that asks for it (`true` or `"true"`) is refused before anything is written.
- **Where it is true,** the form offers it, unchecked whatever the schema's `default`, and a create that asks for it stores that in `settings.json`.
- **Every launch checks it**, as the root's config says at that launch: create, resume, a reply's resume (`ReplyAndResume`) and the resume after a restart. `--dangerously-skip-permissions` is passed only when `settings.json` asks for it and the root allows it for every one of its actions: `settings.json` also names the project's action, so a launch does not take the action's word alone. An overlay with `"allowSkipPermissions": false` still refuses the create for its action; one with `true`, in a root whose other actions do not allow it, lets the create ask, but its launches do not skip. `settings.json` is in the project folder, which the session can write, and a restart relaunches a project from its files unattended, so a planted or copied folder cannot give itself skip.
- **A project whose `settings.json` asks for skip under a root that does not allow it** launches without it, and the server logs a warning once for that project. That includes every project created with skip before `allowSkipPermissions` existed: its next launch waits on the permission prompt for what needs approval, until its root (or its action's overlay) sets `"allowSkipPermissions": true`.

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
- **Only a project token opens `/mcp`**, and only for the project it was issued to. The user's API key does not, and a project token opens nothing else: not the hub, not `/api/*`.
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

Some keys have special meaning: `name` and `prompt` are the project name and initial Claude prompt unless `nameTemplate`/`promptTemplate` override them, `skipPermissions` starts Claude with `--dangerously-skip-permissions` where the root allows it (see [Permissions](#permissions); without it, a tool call that needs approval waits for the user: `WaitingPermission`), and `model` overrides the action's model.

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

See [Environment](#environment) for everything else a script gets.

A create script can override the project's `project_path`, `project_name` or `project_prompt` by writing them to `GODMODE_RESULT_FILE`, one `key=value` per line. The last key may span several lines, which suits a multiline prompt.

Script stdout is streamed to the client as creation progress. Non-zero exit code aborts creation.

### Environment

Neither a Claude process nor a root script (`prepare`, `create`, `delete`, `status`) inherits the server's environment, which holds its secrets: an `Authentication__ApiKey`, a codespace's `GITHUB_TOKEN`. Each starts from an allowlist, then the profile's and root's `environment`, then the `GODMODE_*` variables above:

- **Both** get the OS essentials: `PATH`, `HOME`, `TEMP`/`TMP`/`TMPDIR`, `LANG`, `LC_*`, `TZ`, `TERM`, `USER`, `SHELL`, the `XDG_*` directories; on Windows also `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`, `SystemRoot`, `ComSpec`, `PATHEXT`, `PSModulePath`, the `ProgramFiles` family and their like; the proxy and certificate variables (`HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY`, `ALL_PROXY`, `NODE_EXTRA_CA_CERTS`, `SSL_CERT_FILE`, `SSL_CERT_DIR`); and `DOTNET_ROOT`. That is what `pwsh`, `git` and `gh` need to run and to find their own configuration.
- **Claude** also gets Claude Code's own: `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_BASE_URL`, `CLAUDE_CODE_OAUTH_TOKEN`, `CLAUDE_CONFIG_DIR`, `CLAUDE_CODE_GIT_BASH_PATH`.

A credential a script or a session needs that is not a file in the user's home goes in the root's (or profile's) `environment`, and then reaches both: `GH_TOKEN` or `GITHUB_TOKEN` for `gh` and its git credential helper, `SSH_AUTH_SOCK` for an SSH agent, `GIT_SSH_COMMAND`, a desktop keyring's `DBUS_SESSION_BUS_ADDRESS`. `godmode-dev` passes the codespace's token this way, `"environment": { "GITHUB_TOKEN": "${GITHUB_TOKEN}" }`; on a machine where `gh` is logged in with its own stored credentials (`gh auth login`, the Windows credential manager), the entry expands to nothing and is dropped, and `gh` reads its login from the home directory.

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

When the server stops, it stops every project, and one that was `Running`, `WaitingInput` or `WaitingPermission` keeps that in `ProjectStatus.StateAtShutdown` (in `status.json`) beside `Stopped`. The marker is saved before the project is stopped, from what it was doing as the shutdown began, and nothing that happens during the stop changes it: not claude's answer to the interrupt (an `error_during_execution` result), not its exit, and not a server killed before the stop is done. A project whose stop by the user is still under way (in its grace period) when the shutdown begins is not marked. When it starts again, once it is listening and has recovered the projects, it carries on with them as the project's action says:

- **Working** (`Running`, or `WaitingPermission`, whose prompt the shutdown denied): resumed with `--resume <session-id>`, launched as any resume is, and sent `resumePrompt` as its first input. Three are resumed at a time, each until claude reports `system/init`.
- **Waiting on a question** (`WaitingInput`): no process is launched. The project is `WaitingInput` again with its `CurrentQuestion`, and still a `Question` in `GetAttention`, until a reply (`ReplyAndResume`) resumes it with the answer. An AskUserQuestion's options do not survive: the shutdown denies it before it interrupts claude, and what is left is its first question's text, answered in the chat.
- **`resumeOnRestart: false`**: the project stays `Stopped`.

A project the user stopped, or that was `Idle`, `Stopped` or `Error` when the server stopped, is not resumed. A resume that fails is `Error` with `LastError`, as any resume is (a root config the launch cannot use, a claude that exits at once), and is not tried again. Any launch clears `StateAtShutdown`, and so does a stop by the user. A project waiting its turn is decided when it comes: one the user has stopped, resumed or answered meanwhile is left as it is. A shutdown while the start is still resuming launches nothing more, and the projects not resumed yet keep their marker for the next start. The marker is only a field in `status.json`: a `status.json` restored from a backup or copied from another machine carries it, and that project is resumed on the next start.

Sessions are off the server's console (see [Stopping a Session](#stopping-a-session)), so a Ctrl+C in the server's terminal reaches claude only through the server's shutdown. A claude that exits on its own just before a shutdown still counts as stopped by it: an exit no more than `ExitBeforeShutdownWindowSeconds` (default 5) before the shutdown, with nothing changed since, and the project keeps its question and is resumed like the rest. A server that is killed (no shutdown runs) leaves no `StateAtShutdown`, and its projects are recovered `Stopped`.

## Sessions

### One Project, One Claude

A project has at most one claude process at a time.

- **A create does not make a project that is there.** It is refused ("is in use") when a tracked project has its ID or its folder (on Windows compared as Windows compares paths, so `Fix` is the folder `fix`), before a folder is reused or any script runs, and nothing is written. So is a create script's `project_path` that is a tracked project's folder: the create is then `Error`, under its own ID, saying why, and the project in that folder keeps its claude and its files. "Reuse folder" (`__reuseExisting`) is for a folder no tracked project uses. A create that failed leaves its `Error` project, with its ID: delete it before creating it again.
- **One launch or stop at a time.** Create, resume, a reply that resumes (`ReplyAndResume`), stop, delete and the start carrying on after a restart take the project's lock, so a stop comes before a launch or after it, never in the middle of one, and two resumes launch one claude. A launch still starting, or a claude whose exit is not handled yet, is waited for, never taken for a stale `Running`.
- **A resume with nothing to say is `Idle`.** `ResumeProject` on a stopped project starts claude on its session, and claude writes nothing until it has input: the project is `Idle` ("resumed, waiting for you") until the user writes, rather than `Running` with nothing happening.
- **A launch that does not start says why.** A missing executable, a root config the launch cannot use, or a create script that failed leaves the project `Error` with `LastError`.

### Stopping a Session

Each session runs in a process tree of its own, off the server's console: on Windows claude has a hidden console of its own and is in a Job Object; on Linux it is started through `setsid`, as the leader of a session and process group of its own. A Ctrl+C in the server's terminal reaches the server alone, which then stops its sessions itself. Without `setsid` on the `PATH` (macOS), claude stays in the server's process group, and a stop kills claude and the children it still has.

A stop (`StopProject`, `DeleteProject`, and the server's shutdown) is graceful first:

1. **claude is interrupted, and its input closed.** On Windows the interrupt is Ctrl+Break raised in claude's console; the server starts itself as a helper to raise it (`GodMode.Server.exe --godmode-console-break <pid>`), since a process can raise a console event only in its own console. Elsewhere it is SIGINT to claude's process group.
2. **claude has `StopGracePeriodSeconds` (default 10) to exit.** Its answer to the interrupt, a turn ended as interrupted, does not make the project `Error`.
3. **Then its whole tree is killed**, the Job Object or the process group: every process the session started, one whose parent is gone included. What is left of the tree when claude exits, whether it exited on the interrupt or on its own, is killed then too.

The interrupt is the one claude honours whatever the server was started from. Measured with claude 2.1.282 on Windows: Ctrl+Break in its console ends it in about a second, between turns, while it generates, or while a tool runs, with its session's transcript ending on a whole line and the session resumable. A Ctrl+C there does the same, but a claude whose server was started with Ctrl+C ignored (as some launchers start programs, and Windows passes that on to children) ignores it too. Closing its input alone ends it only after its turn, however long that takes.

The server's shutdown stops every session at once, within its 15-second bound: the grace period is shortened to leave the kill 3 seconds. A delete stops claude before its scripts run, denies a permission prompt still waiting, clears the question, and records `Stopped`: a delete a script refuses leaves the project `Stopped`, and a restart does not resume it. Root scripts that run past their timeout are killed at once, without an interrupt.

A stop denies the permission prompts claude waits on before it interrupts it: killing claude would drop their calls, and the cleanup of a dropped call would show the project `Running` again, its question gone.

### When Something Fails

A project's state follows claude, whatever else fails:

- **A `status.json` that cannot be saved** (on Windows, a file another process holds for longer than the save retries) does not fail the change: it is pushed to every client, its events are raised (a reply waiting on `system/init` gets it), the failure is logged at Error, and the status is saved with the next change.
- **An append to `output.jsonl` that fails** is tried once more on the file opened again, cut back to where the last whole line ended, so every offset stays the file's. A line that still cannot be persisted is not broadcast: its offset would be the previous line's. A failing item does not stop the project's consumer, and a consumer that faults all the same is started again; a subscribe or stop waiting on it fails rather than hangs.
- **A permission prompt left listed** when claude ends its turn is withdrawn by the turn's `result`, so the next reply reaches claude rather than being taken for its answer.
- **A client that stops reading** holds up only its own messages. Pushes (output, status, attention) are started in order and not waited for, so no project waits on a slow connection until 256 of its pushes are unfinished at once.
- **A message sent while claude works** is echoed by claude (`--replay-user-messages`, `"isReplay": true`) as it takes it, and the echo sets `Running`: a result of an earlier turn handled after the send does not leave the project `Idle` through the next turn. claude 2.1.282 folds a message sent in the middle of a turn into that turn: it echoes it at its next step, and one `result` ends both.

## Project Folder Structure

Each project is stored in a folder under its root:

```
{root}/{folder}/
├── .godmode/
│   ├── status.json      # Current project state
│   ├── settings.json    # Per-project settings (action, permission mode, skip-permissions asked for)
│   ├── input.jsonl      # User input log
│   ├── output.jsonl     # Claude output log
│   ├── output-generation # A GUID, new on each create: which output.jsonl a client's offset is in
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

A connection's calls run up to four at a time (`MaximumParallelInvocationsPerClient`), so a `ReplyAndResume` waiting for a resumed claude's session leaves the tab its `StopProject` and its subscribes. One connection's `SubscribeProject` calls still run one at a time, in the order they came.

Projects:
- `Task<ProjectSummary[]> ListProjects()` — Get all projects
- `Task<ProjectStatus> GetStatus(projectId)` — Get project status
- `Task<ProjectStatus> CreateProject(profileName, projectRootName, actionName, inputs)` — Create a project with form inputs (`actionName` null = default action)
- `Task SendInput(projectId, input)` — Send input to Claude (while a permission prompt or question waits, it answers that instead)
- `Task RespondToPermission(projectId, requestId, decision)` — Allow or deny the project's `PendingPermission`
- `Task AnswerQuestion(projectId, requestId, answers)` — Answer the project's `PendingQuestion` (question text → chosen label or free text)
- `Task StopProject(projectId)` — Stop running project: interrupt claude, then kill its process tree after `StopGracePeriodSeconds` (see [Stopping a Session](#stopping-a-session))
- `Task ResumeProject(projectId)` — Resume stopped project; it is `Idle` until the user writes
- `Task SubscribeProject(projectId, fromOffset, subscriptionId, generation)` — Replay `output.jsonl` from `fromOffset` (the byte offset after the last line the client has; 0 for all, `-N` for the last N turns) in `OutputBatch` messages, then `OutputReplayComplete`, then live `OutputReceived` lines, each line once and in order. `subscriptionId` is the client's own, echoed by this subscription's batches and complete; `generation` is the output generation `fromOffset` is in (null when the client holds none), and a positive offset in any other replays from 0
- `Task UnsubscribeProject(projectId)` — Unsubscribe from output
- `Task DeleteProject(projectId, force)` — Stop the project, run delete scripts and remove it; a refused delete leaves it `Stopped`

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
- `OutputBatch(projectId, subscriptionId, generation, fromOffset, lines)` — Replayed `OutputLine`s (`Offset`, `RawJson`) covering `output.jsonl` from `fromOffset`, for the subscription `subscriptionId`, in the file's `generation`; a replay from 0 when more was asked for, or in another generation, means the client's transcript is not from this file
- `OutputReplayComplete(projectId, subscriptionId, generation, offset)` — The subscription's replay is done at `offset`, in `generation`; live lines follow
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

- "will not start: … API key file": the key file cannot be written, or is under `ProjectRootsDir`. Set `Authentication:ApiKeyFile`, or a key. See *Authentication and binding* above.
- "will not start: Authentication:AllowedOrigins lists …": an entry is not an origin (`scheme://host[:port]`, no path).
- "will not start: PermissionPromptKeepAliveSeconds …": it must be more than 0 and less than 300.

### Claude Process Not Starting

- The project is `Error`, and its `LastError` says why
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

- The browser client asks for the API key, which every server requires; a rejected key shows the key page again
- A browser that has the key but cannot connect may be on an origin the server does not know as its own: its log names the origin it refused. Add it to `Authentication:AllowedOrigins`
- Check firewall rules for port 31337
