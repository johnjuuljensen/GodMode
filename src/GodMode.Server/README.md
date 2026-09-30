# GodMode.Server

SignalR server for GodMode. It runs Claude Code sessions in project folders on the machine it runs on and streams their output to its clients, the GodMode app's relay and attention service. It serves no page: the React client is built into the app alone, and no browser is a client.

## Features

- **Real-time Communication**: SignalR hub (`/hubs/projects`) for bidirectional communication
- **Process Management**: Spawn, stop and resume Claude Code processes
- **Config-Driven Project Roots**: Roots from the server's config (scan folders and explicit roots), with per-action config overlays
- **Script-Based Creation**: VCS-agnostic — all prepare/create/delete logic lives in scripts, not server code
- **Cross-Platform Scripts**: Write `.ps1` scripts once; they run under `pwsh` on Windows and Linux
- **State Persistence**: Each session's state lives in its working folder's `.godmode/sessions/<id>/` and is recovered on restart
- **Permission prompts**: Every session asks the user for permission through the server's own MCP endpoint, `/mcp`, whose one tool is claude's `--permission-prompt-tool`

## Configuration

### appsettings.json

```json
{
  "Authentication": { "ApiKey": "" },
  "Roots": { "Scan": { "default": "roots" } },
  "Urls": "http://127.0.0.1:31337"
}
```

Every setting can also come from the instance's config file, an environment variable (`Roots__Scan__default`, `Authentication__ApiKey`) or the command line (`--Roots:Scan:default=/srv/roots`).

### Roots and profiles

A server's roots, and what its profiles carry, come from its config. Each is a **keyed map**, so the config sources below merge entry by entry: a later source replaces the entries it names and keeps the rest. (.NET merges arrays by index, so a list would let the instance's one entry replace appsettings' first.)

| Setting | What it is |
|---|---|
| `Roots:Scan:<key>` = folder | A scan folder: each immediate subfolder with a `.godmode-root/` folder is a root, named after the subfolder. appsettings has one, `default` = `roots`, a scratch folder under the working directory |
| `Roots:Explicit:<name>:Path` = folder | The folder is the root `<name>`, anywhere on disk. A folder with no `.godmode-root/` has the default action |
| `Roots:Explicit:<name>:Profile` | The explicit root's profile, when its own `config.json` names no `profileName` |
| `Profiles:<name>:Description` | The profile's description, as the app shows it |
| `Profiles:<name>:Environment:<VAR>` = value | An environment variable of every session and root script in the profile (a `CLAUDE_CONFIG_DIR`, a service's token) |

- **A root's profile** is its `config.json`'s `profileName`, else its explicit entry's `Profile`, else `Default`. A profile is listed when it has a root; one named under `Profiles` alone is only settings.
- **One name, one root per server, and one folder, one root.** An explicit root wins a clash, and between scan folders the first key in ordinal order does. Each loser is logged once as a warning, with both paths and the settings they came from. An explicit root in a scan folder under its own name is the one root, and no clash.
- **An entry set to `""`** is none, so a later source can turn off one an earlier source set (`"Roots": { "Scan": { "default": "" } }`). An explicit root whose folder does not exist is logged once, and listed once it does.
- **Relative folders** resolve against the working directory.
- **Live.** The roots are read again, from the config as it is then, on every list of profiles or roots, on every reload of the config (the instance's file reloads when it changes), and every `RootsPollSeconds` (default 5; `0` turns the poll off). The poll is there because a file watcher misses changes on a network drive. When the roots or profiles differ from the last ones read (a root added, edited or removed, a `profileName` changed, an explicit root added to the config file), every client gets them pushed (`RootsChanged`), so the app shows them without a reconnect (*Live roots*, below).
- **A root's own `environment` wins** over its profile's.
- **`ProjectRootsDir` and `.profiles/` are gone,** and neither is read. A `ProjectRootsDir`, or a profile's `Roots` (`Profiles:<p>:Roots:<name>`, explicit roots before March 2026), still set in a config source is logged at startup as a warning that names what takes its place. Profiles and roots are maintained by hand on the host: the server reads their config and never writes it.

### Config sources, and the instance's config file

A server's settings come from these sources, each overriding the ones before it:

1. `appsettings.json`, next to the server (its content root);
2. `appsettings.{Environment}.json`, when there is one;
3. **the instance's config file**, when the server is started with one: `--config <path>`, else the `GODMODE_CONFIG` environment variable. A relative path is resolved against the working directory. It is reloaded when it changes, and its folder is watched for that (recursively): keep it in a folder of its own, such as `~/.godmode-server/`, not directly in your home folder. What the server reads once at startup (`Instance`, `Authentication:*`, `Urls`) takes a restart to change; the roots and profiles are read again on every list. A named file that does not exist stops the server at startup;
4. environment variables;
5. the command line.

Config belongs to a server instance, named when it starts: nothing is read per user. There is no default config file, and the server reads no user secrets (it has no `UserSecretsId`). A server started without a config file runs on appsettings, whose one scan folder (`Roots:Scan:default`) is `roots` under the working directory, a scratch folder of its own. So every worktree's `dotnet run` starts empty, and none of them finds the roots of the server you use.

A config file is appsettings-shaped JSON:

```json
{
  "Instance": "main",
  "Roots": {
    "Scan": { "repos": "C:\\Users\\me\\source\\repos" },
    "Explicit": { "notes": { "Path": "D:\\notes", "Profile": "Private" } }
  },
  "Profiles": {
    "Work": {
      "Description": "The day job",
      "Environment": { "CLAUDE_CONFIG_DIR": "C:\\Users\\me\\.claude-work" }
    }
  },
  "Authentication": { "ApiKey": "..." }
}
```

A secret a profile's sessions need can stay out of the file: `Profiles__Work__Environment__JIRA_TOKEN` in the server's environment is the same setting.

`Instance` (default `default`) names the server: in the lock of each root it holds, and in its logs (`Server instance main` at startup, beside `Config file: …`).

### One server per root

A server holds a lock on every root it manages: `{root}/logs/server.lock`, kept open exclusively for as long as the server runs. The operating system lets it go when the server exits, however it exits (a crash or a kill included), so it never goes stale. Beside it, `{root}/logs/server.json` names the holder (`instance` and `processId`). `{root}/logs/` is the server's own folder, and the server keeps a `.gitignore` of `*` in it, so neither shows in `git status` when the root's folder is under source control.

A root another live server holds is skipped: it is not listed, and none of its projects is recovered. The server logs it once as a warning, with the holder's instance and process when `server.json` can be read, and tries again every time it rebuilds its roots (on each list of profiles or roots). Once the other server has gone, the root is listed on the next rebuild, and its sessions are recovered then, as those of any root that appears. A root a rebuild no longer finds is let go only when no project of the server's is in it: a root that blinks (a folder replaced by an editor, a share that drops out) while its sessions run stays held. A server lets its roots go once it has stopped its projects at shutdown.

### Live roots

Live root updates start once the startup's recovery has run: from then on every read of the roots (above) also brings the tracked sessions in line with them.

- **A root that appears** (a folder with a `.godmode-root/` in a scan folder, an explicit root added to the config, a root another server let go) has its sessions recovered as the start recovers them, each pushed as `ProjectCreated` after the `RootsChanged` that lists its root. One the last shutdown interrupted is not resumed: that is the start's alone.
- **A session is its root's by folder.** Its delete, status, launch and restart read the config and scripts of the root folder it was created or recovered in, whatever that root is called now. A root name that comes to name another folder (a new explicit root that wins the clash with a scanned one) is another root: the sessions of the old folder never run its scripts.
- **A root that goes** (its folder or `.godmode-root/` removed, its entry taken out of the config, or it loses a name clash): its sessions without a claude, running or launching, leave the list (`ProjectDeleted`), their files left as they are, and come back if the root does. A session whose claude runs carries on, under its root's config as it was read from its folder, and leaves once claude has exited. The server holds the root's lock until its last session has left, so no other server takes it meanwhile.
- **A root's `profileName` edited, or its explicit entry renamed**: its sessions' IDs (`{profile}/{root}/{id}`) name the profile and the root, so each session takes the ID the root has now, as a restart would give it. One without a claude does so at once: its old ID is pushed as `ProjectDeleted`, its new one as `ProjectCreated`, and its `status.json` is rewritten. One whose claude runs keeps its ID, which its MCP config carries, until claude exits, then does the same.
- **Two reads in a row** must agree before a session leaves or changes its ID: a `config.json` saved half-written reads as the default config, in the `Default` profile, and a folder can blink.
- **A busy session waits for a later read.** A read lets a session go under its lock, which a delete, stop or launch of it holds: it waits 2 seconds at most, then leaves it for the next read, so a delete script that runs long holds up no read of the roots.
- **A delete script gets its session's profile environment** even once the root has left that profile (moved to another, or removed) while claude ran: from the snapshot while the profile has roots, else from the config's `Profiles:<name>:Environment`.

### Executables

The server starts two executables, each named by a setting: a name looked up on the server's `PATH`, or a full path.

| Setting | Starts | Default | Docker image |
|---|---|---|---|
| `Claude:Executable` (`Claude__Executable`) | every session | `claude` | `/usr/bin/claude` |
| `PowerShell:Executable` (`PowerShell__Executable`) | `.ps1` root scripts | `pwsh` | `/usr/bin/pwsh` |

A lookup takes the first match on the `PATH`, so a directory on it that a session can write (`~/.local/bin`, where Claude Code's installer puts `claude`) can decide what runs. Where that matters, give full paths to files the session cannot write, as the Docker image does. On a PC, a VM or a codespace, the server runs as the same user as its sessions and the defaults are the usual lookup. The server starts no Node itself. `.sh` scripts run under `bash`, found on the `PATH`.

### Authentication and binding

Every endpoint and the SignalR hub require authentication, whatever the server is bound to, loopback included: nothing on this machine gets in without the key either. Only `/health` is anonymous. The server picks one mode at startup:

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
- **Another place:** `Authentication:ApiKeyFile`. It must not be under any root's folder, a scan folder or an explicit root, where sessions work: the server refuses to start if it is, and names the setting. A scan folder or explicit root added to the config while the server runs, whose tree holds the key file, is left out and logged once as a warning, so it never gets that far.
- **Read it again** with `cat ~/.local/share/GodMode.Server/api-key` (Windows: `type %LOCALAPPDATA%\GodMode.Server\api-key`). Write your own key into it, or delete it for a new one on the next start.
- **A configured key always wins**, and the key file is then neither read nor written. So does a codespace, which uses no key.
- **Docker:** a replaced container has a new home, so a new key. Run it with `-e Authentication__ApiKey=<key>`, or keep the key file on a named volume: `-v godmode-key:/home/godmode/.local/share/GodMode.Server`. The image creates that directory, owned by `godmode` with mode 0700, and a new named volume starts with its owner and mode. A bind mount (`-v /srv/godmode-key:…`) keeps the host directory's owner instead, which must be writable by the container's `godmode` user.

A key of your own can go in the instance's config file or `appsettings.json` (`"Authentication": { "ApiKey": "..." }`), in the `Authentication__ApiKey` environment variable, or on the command line as `--Authentication:ApiKey=<key>`. `openssl rand -hex 32` makes one.

**No browser.** A browser sends an `Origin` header on every WebSocket upgrade, which CORS does not cover, and on any request but a same-origin GET. The server serves no page and refuses every request that carries an `Origin`, with 403 and before authentication, whatever it names: its own bindings, `localhost`, a codespace's forwarded port, `Origin: null`, in Development too. No setting allows one (there is no `Authentication:AllowedOrigins`). So no page in a browser, served from anywhere, can use the server, with the key or without. A request with no `Origin` (the app's relay and attention service, a session's claude on `/mcp`, `curl`) needs its credential alone. The server logs a warning for each request it refuses, naming the origin.

**Bindings.** The shipped config binds `http://127.0.0.1:31337`, so a fresh `dotnet run` is reachable only from the same machine, and still needs the key. The GodMode app stores the key per server (the API key you enter when adding it) and adds it when relaying.

**Reaching the server from other devices.** Bind to a private-network address, such as the machine's Tailscale IP, rather than `0.0.0.0`. Keep the loopback binding as well: projects' claude calls the server's MCP endpoint on it. Add the server in the app on the phone by that address (`http://<tailscale-ip>:31337`), or by a name for it (MagicDNS); the app accepts several URLs for one server and uses the first that answers.

```bash
dotnet run --project src/GodMode.Server/GodMode.Server.csproj -- \
  --urls "http://127.0.0.1:31337;http://$(tailscale ip -4):31337"
```

**Docker:** the image sets `URLS=http://+:31337` (all interfaces). Add it in the app by whatever address its published port is reached on (`http://localhost:31337`, a host name, a LAN address, another published port such as `-p 8080:31337`). To change the binding, use the unprefixed `URLS` variable or `--urls`. `ASPNETCORE_URLS` loses to the `Urls` in `appsettings.json`.

**What the key does not stop.** Sessions run as the server's own OS user. A session that can run arbitrary commands can read the key file, `appsettings.json` or the server's environment, and with the key drive the hub, answering its own permission prompts. The permission prompt is a gate as long as the commands it approves don't do that; it is not a sandbox. The server hands neither a session nor a root script the key (their environment is an allowlist, see [Environment](#environment), and the key file is under no root), but real isolation, a separate OS user or container per session, is out of scope. The same goes for a codespace's `GITHUB_TOKEN`: the server refuses it, but sessions that are given it hold it.

## Project Roots

### Multi-File Config Structure

```
{scan folder}/                        # or an explicit root anywhere
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
    └── {project-folder}/             # Working folders created from this root, each with its session in .godmode/sessions/<id>/ (ID {profile}/{root}/{id})
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
| `sharedFolder` | If true, the action's sessions share their working folder (an assistant's workspace): a create may go into a folder other sessions of shared actions use, and a delete removes only the session's state, never the folder. Default `false`: a folder another session uses is refused. See [Several sessions in one folder](#several-sessions-in-one-folder) |
| `transient` | Whether the action's sessions are short-lived (chats, experiments): the app folds them under "N older" after a day of quiet, not a week. Default `false`. In `config.json` it is every action's; an overlay sets it for its action. Nothing is deleted by it. See [The trash, and folding](#the-trash-and-folding) |
| `session` | Whether the action starts a session. Default `true`. `false`: its scripts run, and nothing more, as a provisioning action does. See [Actions that start no session](#actions-that-start-no-session) |
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
- **Kept with the project.** A create stores the action's mode in the session's `settings.json` (in `.godmode/sessions/<id>/`, `permissionMode`), and every launch of it uses that one, even after the root's config has changed. A project with none stored, such as one created before the root had a mode, takes the root's current one. The stored one is checked again at each launch, since the session can write that file: an unknown one, or `bypassPermissions`, is left out, with a warning.
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
- **The token** is issued afresh for each launch and lives only in memory and in this file. The file is `mcp-config.json` in the session's state folder (`.godmode/sessions/<id>/`), owner-only where the OS allows, and is deleted when the process exits. No environment variable carries it.
- **Only a project token opens `/mcp`**, and only for the project it was issued to. The user's API key does not, and a project token opens nothing else: not the hub, not `/api/*`.
- **The tool** takes claude's flat arguments, `tool_name`, `input` (an object) and `tool_use_id` (optional). It waits until the user answers, however long that takes, and returns claude `{"behavior":"allow","updatedInput":{…}}` or `{"behavior":"deny","message":"…"}` as text.
- **While it waits,** it sends a progress notification every `PermissionPromptKeepAliveSeconds` (default 30). claude gives up on a tool call that sends no response or progress for 300 seconds.
- **When claude cancels the call**, or its connection drops, the request is withdrawn (denied).
- **What clients see** is `ProjectStatus.PendingPermission`: the tool's name and a one-line `Summary` (`Bash: git push origin x`, ending with ` …` when it leaves something out), which a notification shows and speech reads. It carries no tool input, and neither does `status.json`: a `Write` can be megabytes, and the request is pushed in every `StatusChanged`, `ListProjects` and `AttentionChanged`. The input stays in the server's memory for as long as the call waits, which is as long as it can be answered: a restart ends the call, and claude asks again with its input.
- **Before Allow**, a client fetches `GetPermissionDetail`: the whole command for `Bash` and `PowerShell`, the path and the whole new text for `Write` and `NotebookEdit`, the path and each replacement for `Edit` and `MultiEdit` (`Replace:`, or `Replace every occurrence of:` with `replace_all`, the old text, `With:`, the new text), and the input as indented JSON for any other tool, cut at 16384 characters with `DetailTruncated` set. The call runs with all of it.
- **One answer counts.** When two clients answer at once, the one that came second fails, as does any answer to a request that was answered already or withdrawn: claude got the other.

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
  "required": ["name"]
}
```

The default requires only the name; a root's own `schema.json` may require more, `prompt` included. A create that leaves a required field out (missing, null, or only whitespace) is refused before anything is written or run, by the server as by the app's form.

**A session with no prompt starts idle.** With no prompt from the form, the `promptTemplate` or a create script's `project_prompt`, the session is made as any other (folder, state, settings) and claude is started with no input: it waits on stdin for the first message, and the project is `Idle`, which asks nothing of the user, so it is no attention item. Claude is sent no turn the user did not write: not an empty one, and not a stand-in. The first message the user sends is claude's first turn, in that process. A session with no conversation yet that has no process (stopped, restarted, or its claude failed at its start) has nothing for `--resume` to find. Its first message, or a Resume, starts claude on the session's own id (`--session-id`) and sends no "continue": the message is its only turn. Resume on one that is running sends nothing. A session has a conversation once claude was sent a message (`input.jsonl`) or wrote output of its own (`output.jsonl`, other than the `error` lines its stderr is logged as).

Some keys have special meaning: `name` and `prompt` are the project name and initial Claude prompt unless `nameTemplate`/`promptTemplate` override them, `skipPermissions` starts Claude with `--dangerously-skip-permissions` where the root allows it (see [Permissions](#permissions); without it, a tool call that needs approval waits for the user: `WaitingPermission`), and `model` overrides the action's model.

### Scripts

Scripts are the abstraction layer for all VCS and setup operations. The server doesn't know about git, mercurial, or any other tool.

**Cross-platform**: Specify scripts without extension in the config. The server resolves to the right file based on OS:
- Windows: tries `.ps1`, `.cmd`, `.bat`
- Linux/Mac: tries `.sh`, then `.ps1`

`.ps1` runs under `pwsh` (`PowerShell:Executable`, see [Executables](#executables)), `.sh` under `bash`, `.cmd`/`.bat` under `cmd`. A single `.ps1` therefore works everywhere; see the script constraints in `CLAUDE.md`. A script named with an explicit extension in the config (for example `"prepare": "scripts/prepare.ps1"`) is used as-is.

**Environment variables** available to all scripts:

| Variable | Description |
|----------|-------------|
| `GODMODE_ROOT_PATH` | Root directory path |
| `GODMODE_PROJECT_PATH` | Project directory path |
| `GODMODE_PROJECT_FOLDER` | The project's folder name, the last segment of `GODMODE_PROJECT_PATH`. Not the session's ID, which is `{profile}/{root}/{id}` (see below); no script is given that |
| `GODMODE_PROJECT_NAME` | Display name |
| `GODMODE_SESSION_ID` | The session's id, `yymmdd-<kind>-<slug>-<suffix>` (see [Project Folder Structure](#project-folder-structure)): its state is in `$GODMODE_PROJECT_PATH/.godmode/sessions/<id>/`. A create script is given the id as its action makes it (the action's name as the kind, the name as it was asked for); a `kind` or `project_name` in its result gives the session its final one, with the same date and suffix, which every later script (delete, status) is given. So the id a create script sees is final only when its result names neither: a create script that keys something by the id (in a shared folder, where the folder names no session) returns no `kind` or `project_name`, or leaves the keying to a script that runs later |
| `GODMODE_INPUT_*` | All form inputs (key in upper snake case, e.g. `GODMODE_INPUT_ISSUE_NUMBER`) |
| `GODMODE_RESULT_FILE` | Create scripts only: a file the script can write `key=value` lines to (see below) |
| `GODMODE_FORCE` | Delete scripts only: `true` when the user forced the delete |
| `GODMODE_SHARED_FOLDER` | `true` when the session shares its working folder with others (its action's `sharedFolder`), else `false`. A delete script is told `true` also when the folder has another session, or its action shares folders now: it must then leave the folder, and whatever the other sessions use, alone, since the server removes only `.godmode/sessions/<id>/`. See [Several sessions in one folder](#several-sessions-in-one-folder) |
| *(from `environment`)* | All vars from the profile's `Profiles:<name>:Environment` and the config's `environment` block, which wins a clash |

See [Environment](#environment) for everything else a script gets.

A create script can override the project's `project_path`, `project_name` or `project_prompt`, and name the session's `kind`, by writing them to `GODMODE_RESULT_FILE`, one `key=value` per line. Only the keys below are read: a line starts a key only when it begins with one of them followed by `=`.

- **`project_prompt` and `message` run to the end of the file.** Their value is everything from their `=` to the end, whatever the lines after it hold: `a=b`, code, a URL's `?q=1`, even a line starting `kind=`. So a script writes them last, after the single-line keys, and writes at most one of them (a second is part of the first's value).
- **`project_path`, `project_name` and `kind` are one line each**, read wherever they are before a multi-line key. Blank lines and lines starting `#` before it are skipped.
- **Any other `key=` line is ignored**, and logged once as a warning by its keys (cut to 40 characters), never by its values.

| Key | What it sets |
|-----|--------------|
| `project_path` | The working folder, strictly inside the root, or the root itself for an action with `"sharedFolder": true` (see [Project Folder Structure](#project-folder-structure) and [A root as its own workspace](#a-root-as-its-own-workspace)) |
| `project_name` | The display name, and the id's slug |
| `project_prompt` | The first prompt |
| `kind` | The session's kind (`bug`, `feat`, `experiment`, `chat`…): the label the app shows, and the id's kind. Without one, the kind is the action's name. Kept as the id has it: lowercase `[a-z0-9-]`, 12 characters at most |
| `message` | Only for an action that starts no session: what it made, which the app shows when it has run (see [Actions that start no session](#actions-that-start-no-session)), cut to 500 characters, and not logged. An action that starts a session ignores it |

For example, an issue script that names the kind from the issue's labels:

```powershell
$kind = if ($labels -contains 'epic') { 'epic' } elseif ($labels -contains 'bug') { 'bug' } else { 'feat' }
"kind=$kind" | Add-Content $env:GODMODE_RESULT_FILE
```

Script stdout is streamed to the client as creation progress, and a create's prepare and create scripts log to `{root}/logs/<id>.log`, their result file being `{root}/logs/<id>.result`: the session's own, by the id it keeps (renamed when the result gives it its final id), so two sessions of one folder never share one. Non-zero exit code aborts creation.

### Actions that start no session

An action with `"session": false` only runs its scripts. A provisioning root uses one to change the host, for instance **New experiment root**, which makes a new root beside it in its scan folder; promoting an experiment to a worktree root is another, given an existing folder. The server writes no config for it (no hub method does): the script does, as a root script may. The same script can be run by an assistant session after a conversation, which needs nothing from GodMode.

```json
{ "session": false, "create": "new-root/create.ps1" }
```

```powershell
$ErrorActionPreference = 'Stop'
$root = Join-Path (Split-Path $env:GODMODE_ROOT_PATH -Parent) $env:GODMODE_INPUT_NAME
New-Item -ItemType Directory -Force (Join-Path $root '.godmode-root') | Out-Null
'{ "profileName": "Experiments" }' | Set-Content (Join-Path $root '.godmode-root/config.json')
"message=Root $env:GODMODE_INPUT_NAME is ready" | Set-Content $env:GODMODE_RESULT_FILE
```

- **The scripts run as a create's do:** `prepare`, then `create`, in the root (working directory and `GODMODE_ROOT_PATH`), with the root's and its profile's environment, the form's inputs (`GODMODE_INPUT_*`) and `GODMODE_RESULT_FILE`, from the same environment allowlist. They get no `GODMODE_PROJECT_*`, `GODMODE_SESSION_ID` or `GODMODE_SHARED_FOLDER`: there is no project. Output streams as `CreationProgress`, and a non-zero exit fails the create.
- **Nothing is tracked, and no claude starts.** No working folder is made, no `.godmode/`, no project is listed or announced (`ProjectCreated`), and a failed script leaves no `Error` project behind: the create fails with its message, and its log stays.
- **The run has an id as a session would**, `yymmdd-<kind>-<slug>-<suffix>` (the action's name as the kind, the `name` input, if any, as the slug), for its log and result file (`{root}/logs/<id>.log`, `.result`) and its progress, whose ID `{profile}/{root}/<id>` names no project. The progress ends when `CreateProject` returns, which carries the message: no event marks the end.
- **Of the result file, only `message` is read.** `project_path`, `project_name`, `project_prompt` and `kind` are ignored, and their checks do not apply: nothing is made a project's folder, so a script may name a folder outside the root (the root it made) without being refused.
- **`CreateProject` returns no project** (`CreateProjectResult.Project` is null) and the script's `message`. The app shows the message, or that the action finished, and stays on the form; it offers no model for such an action (the listed action's `Session` is `false`, its `Model` null and `AllowSkipPermissions` false).
- **The roots are read again once it has run**, so a root it made reaches every client as `RootsChanged` at once, not at the next poll ([Live roots](#live-roots)).
- **What would give it a working folder is a config error**: `"session": false` with `"sharedFolder": true`, with `"scriptsCreateFolder": true`, or with no `create` script. A create of the action is refused saying why, and the listing leaves that action out, with a warning in the log, as it leaves out an overlay it cannot read: the root keeps its profile and its other actions. The merged action is checked, so a base `config.json` that sets one of these for its worktree actions needs `false` in the session-less action's overlay. `claudeArgs`, `model`, `permissionMode`, `allowSkipPermissions`, `promptTemplate`, `delete`, `status` and the resume settings mean nothing to it and are ignored, since a base config shares them with every action.

### Environment

Neither a Claude process nor a root script (`prepare`, `create`, `delete`, `status`) inherits the server's environment, which holds its secrets: an `Authentication__ApiKey`, a codespace's `GITHUB_TOKEN`. Each starts from an allowlist, then the profile's environment (`Profiles:<name>:Environment`), then the root's `environment`, then the `GODMODE_*` variables above:

- **Both** get the OS essentials: `PATH`, `HOME`, `TEMP`/`TMP`/`TMPDIR`, `LANG`, `LC_*`, `TZ`, `TERM`, `USER`, `SHELL`, the `XDG_*` directories; on Windows also `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`, `SystemRoot`, `ComSpec`, `PATHEXT`, `PSModulePath`, the `ProgramFiles` family and their like; the proxy and certificate variables (`HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY`, `ALL_PROXY`, `NODE_EXTRA_CA_CERTS`, `SSL_CERT_FILE`, `SSL_CERT_DIR`); and `DOTNET_ROOT`. That is what `pwsh`, `git` and `gh` need to run and to find their own configuration.
- **Claude** also gets Claude Code's own: `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_BASE_URL`, `CLAUDE_CODE_OAUTH_TOKEN`, `CLAUDE_CONFIG_DIR`, `CLAUDE_CODE_GIT_BASH_PATH`.

A credential a script or a session needs that is not a file in the user's home goes in the root's `environment` (or the profile's, `Profiles:<name>:Environment`), and then reaches both: `GH_TOKEN` or `GITHUB_TOKEN` for `gh` and its git credential helper, `SSH_AUTH_SOCK` for an SSH agent, `GIT_SSH_COMMAND`, a desktop keyring's `DBUS_SESSION_BUS_ADDRESS`. `godmode-dev` passes the codespace's token this way, `"environment": { "GITHUB_TOKEN": "${GITHUB_TOKEN}" }`; on a machine where `gh` is logged in with its own stored credentials (`gh auth login`, the Windows credential manager), the entry expands to nothing and is dropped, and `gh` reads its login from the home directory.

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
- **A recovered one is checked again.** `status.json` is in the project folder, which its session can write, so on recovery a `PullRequest` whose `url` is not an http(s) URL of at most 2048 characters is dropped (and logged), as the script's output would have been.

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

- **A create does not make a project that is there.** It is refused ("is in use") when a tracked project has its ID, or its folder unless both share it (`sharedFolder`, [below](#several-sessions-in-one-folder)) (on Windows compared as Windows compares paths, so `Fix` is the folder `fix`), before a folder is reused or any script runs, and nothing is written. So is a create script's `project_path` that is a tracked project's folder: the create is then `Error`, under its own ID, saying why, and the project in that folder keeps its claude and its files. "Reuse folder" (`__reuseExisting`) is for a folder no session uses: one with a session's state on disk in `.godmode/sessions/`, tracked or not, is in use too. A create that failed leaves its `Error` project, with its ID: delete it before creating it again.
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

Every session has a working folder in its root, and keeps its state in that folder's `.godmode/sessions/<id>/`. The same layout serves every kind of root: a worktree is a working folder with one session, and an assistant's workspace a working folder with several ([below](#several-sessions-in-one-folder)), which may be the root itself ([A root as its own workspace](#a-root-as-its-own-workspace)).

```
{root}/{folder}/
├── .godmode/
│   ├── .gitignore               # "*": everything in .godmode is out of git
│   ├── trash/
│   │   └── {id}/                # A deleted shared session's state, until RestoreProject or the purge
│   └── sessions/
│       └── {id}/                # One session's state, e.g. 260929-feat-left-list-k7q2
│           ├── status.json      # Current state
│           ├── settings.json    # The session's settings (action, permission mode, skip-permissions asked for, shared folder)
│           ├── input.jsonl      # User input log
│           ├── output.jsonl     # Claude output log (GodMode's own; Claude's transcripts are not read)
│           ├── output-generation # A GUID, new on each create: which output.jsonl a client's offset is in
│           ├── session-id       # Claude's session GUID, for --resume
│           └── mcp-config.json  # While claude runs: the session's MCP config, with its token
└── (project files)              # Working directory for Claude
```

### Several sessions in one folder

An assistant root runs several sessions in one workspace: its action says `"sharedFolder": true`, and its create script returns the same `project_path` for every session, a folder strictly inside the root such as `{root}/workspace` (with `scriptsCreateFolder`, the script makes it; without a script, sessions of one name share the folder of that name).

```json
{ "sharedFolder": true, "scriptsCreateFolder": true, "create": "chat/create.ps1" }
```

```powershell
$workspace = Join-Path $env:GODMODE_ROOT_PATH 'workspace'
New-Item -ItemType Directory -Force $workspace | Out-Null
"project_path=$workspace`nkind=chat" | Set-Content $env:GODMODE_RESULT_FILE
```

- **Opt-in per action.** Without `sharedFolder`, a create whose folder another session uses, tracked or only its state on disk, is refused ("is in use"): that keeps a worktree root from two sessions in one worktree. Shared and unshared never mix: a shared create may not join a session that owns its folder (its delete would remove the folder), and an unshared one may not join a folder that sessions share.
- **Each session is its own.** Its state in `.godmode/sessions/<id>/`, its `output.jsonl`, its MCP config and token, its claude process, and its create log and result file (`{root}/logs/<id>.log`, `.result`). A create's claim is its ID's; creates into one shared folder at once share the folder's. The claude processes run in the same working folder; Claude Code keeps a transcript per session, so their histories do not collide, and edits to the same files at once are the user's to avoid.
- **A delete removes only the session's state, into the trash.** The delete script runs with `GODMODE_SESSION_ID` and `GODMODE_SHARED_FOLDER=true`, then the server moves `.godmode/sessions/<id>/` to `.godmode/trash/<id>/` and touches nothing else: never the folder or its files, not even with the folder's last session. `RestoreProject` undoes it ([below](#the-trash-and-folding)). A shared create that failed before its session had its state, in a folder that create made and no other session has come into, takes that folder with its delete. A session counts as sharing when its `settings.json` says it was created so (`sharedFolder`, kept there so a config changed later does not turn a workspace into one a delete removes), its action shares folders now, or another session has the folder. The folder-removal rules below are for folders one session owns.
- **A shared folder that is missing is made**, and one that is there is used as it is, so two creates making one new workspace at once both have it.
- **Recovery** finds every session in the folder, as it finds any other. A session whose `settings.json` is missing or cannot be read is taken as sharing its folder, so its delete removes only its state.
- **The sessions can read each other's state.** It is all inside their shared working directory: another session's `output.jsonl`, and its `mcp-config.json` with its token while its claude runs. That token only lets a session ask permission prompts as the other one, never answer them. Put sessions that must not see each other in separate folders.

### A root as its own workspace

An existing repo can be a root without moving it: a `.godmode-root/` in the repo, and an action whose sessions share their folder and whose create script returns **the root itself** as `project_path`. Its sessions run at the repo's top level, several at once, as in any shared folder. There is no flag: a `project_path` equal to the root is accepted for an action with `"sharedFolder": true`, and refused for any other ("is the project root itself, which only an action that shares its folder may work in"), whose delete would remove the folder.

```json
{ "sharedFolder": true, "scriptsCreateFolder": true, "create": "scripts/create.ps1" }
```

```powershell
# .godmode-root/scripts/create.ps1
$ErrorActionPreference = 'Stop'
"project_path=$env:GODMODE_ROOT_PATH`nkind=chat" | Set-Content $env:GODMODE_RESULT_FILE
```

- **`scriptsCreateFolder: true`.** Without it the server makes a folder for the session's name in the root before the create script runs, as it does for every action whose scripts make no folder, and a `project_path` naming the root leaves that folder behind, empty, in the repo.
- **What the server keeps in the root:** `.godmode/` (the sessions' state and trash, with its own `.gitignore` of `*`) and `logs/` (the create logs and result files, and the root's lock, with a `.gitignore` of `*` too). Both stay out of git on their own. `.godmode-root/` is yours: to keep it out of git without a commit, add it to the repo's local exclude, `.git/info/exclude`, where the server writes nothing:

  ```
  .godmode-root/
  ```

  Then `git status` in the repo shows nothing of GodMode's. A repo that tracks a `logs/` or `.godmode/` of its own gives it to the server: the server appends `*` to that folder's `.gitignore`, and git then ignores every new file in it.
- **A delete never removes the root, or any of its files.** A session in the root is always taken as sharing it, whatever its `settings.json` says, its action says now, or `GODMODE_FORCE` is: the delete script runs with `GODMODE_SHARED_FOLDER=true`, and the server moves only `.godmode/sessions/<id>/` to `.godmode/trash/<id>/`. A create into the root that failed takes nothing with its delete but a folder the create made for its name, and only while that folder is still empty but for its `.godmode`. The purge deletes only `.godmode/trash/<id>/`, and neither follows a link at `.godmode` or below it.
- **A delete script is told `GODMODE_PROJECT_PATH` = the root.** One that removes its project's folder must check `GODMODE_SHARED_FOLDER` first, or it deletes the repo; a root that is its own workspace needs no delete script at all.
- **Recovery** finds the root's sessions in its own `.godmode/sessions/`, as it finds any working folder's.
- **The root's `.godmode` is its own**: no session is given a folder of that name ([below](#project-folder-structure)), so no "Reuse folder" can take the root's sessions.
- **No session that owns its folder, while the root is its own workspace.** As long as a session works in the root (tracked, or with its state or trash in the root's `.godmode/`), a create for an action that does not share its folder is refused ("is its own workspace"), reused or new, before anything is written: its folder would be one of the repo's, and its delete would remove it. That holds for the default action too, which a missing `config.json`, or a typo in `sharedFolder`, falls back to.

### The trash, and folding

A session that shares its folder is deleted at once in the app, with "Deleted · Undo" for 10 seconds: its delete is undone, not confirmed. A worktree's delete removes the folder, which nothing brings back, so the app asks first.

- **The trash.** The delete of a session that shares its folder moves its state, whole, to `.godmode/trash/<id>/` in its working folder, with a `trashed-at` file (UTC, round-trip). `DeleteProject` says so (`DeleteProjectResult.Trashed`). Nothing in the trash is a session: it is not recovered, listed or resumed. Its id stays taken in its root, so no new session gets it while it can be restored.
- **Restore.** `RestoreProject(projectId)` moves the state back to `.godmode/sessions/<id>/` and tracks it again, `Stopped`, **under the same ID**, pushed to every client as `ProjectCreated`. Its settings then say it shares its folder, so its next delete trashes it again. The delete script is not undone. It reads the roots first and fails, changing nothing, when:
  - the ID's `{profile}/{root}/` names no root the server lists now, compared as written: the root was removed, its `profileName` changed or it was renamed, and it would come back under another ID;
  - the session is not in that root's trash (purged, or its delete removed its folder), or a session of its id has its state there;
  - the folder no longer takes it: a create into it is in progress that owns it, or a session that owns its folder is in it now.
- **The purge.** The server deletes trashed sessions older than `TrashRetentionSeconds` (86400, a day) at every start, before recovery, and every `TrashPurgeSeconds` (3600; `0` leaves only the start's). A trash without its `trashed-at` is dated by its folder. The app offers Undo for 10 seconds; the day is for a restart in between, and a restore by hand.
- **Folding** is the app's alone: nothing on the server hides or deletes a session. It folds a session under "N older" in its root when it has no claude (`Stopped` or `Error`), needs nothing of the user, is not open, and has been quiet (`UpdatedAt`) a week, or a day when its action is `transient` (`ProjectSummary.ActionName`, and `CreateActionInfo.Transient` of its root's listed action).

**`.godmode/.gitignore` ignores everything in `.godmode`**, which holds the MCP config with the session's token while claude runs. The server makes sure of it when it sets up the session and on every launch, before it writes that config: it writes the file when missing (a checkout can bring a `.godmode/` without one), and appends the `*` rule to one that lacks it, keeping its lines.

**A session is a folder in `.godmode/sessions/`** of the root itself or of a working folder directly inside it, named as an id is (below), with a `status.json` in it. A folder the root keeps for itself (below) is no working folder: a `logs/.godmode/sessions/…` from before those names were refused is not recovered, so it is never listed, resumed or deleted. Nothing deeper is recovered, and the server moves no folder anywhere. **The old flat layout is not read:** a `.godmode/status.json` directly in `.godmode/` is no session, and nothing migrates it. An id found in two working folders of one root (a folder copied) is recovered from the first, in ordinal order, and the other is logged and left untracked.

**Session ID.** A session's id is GodMode's own, `yymmdd-<kind>-<slug>-<suffix>`: the server's local date when it was created, its kind, a slug of its name (lowercase `[a-z0-9-]`, with `æ`/`ø`/`å` spelled `ae`/`oe`/`aa` and other accents dropped, at most 24 characters, and left out when the name has none), and 4 random base32 characters. It is short, for Windows' path limits, and unique within its root: a create picks another suffix when a tracked session, a create in progress, or a state folder on disk in one of the root's working folders has it. It is not Claude's session GUID, which is in `session-id`: GodMode replaces that when a resume finds no conversation.

**The session's opaque ID** is `{profile}/{root}/{id}`. Two sessions with one name in different roots or profiles are separate, with their own process, output and SignalR group, and so are two with one name in one root on one day. Clients treat the ID as opaque and pass it back as they received it. The server derives it from where the state folder is on every start and writes it to `status.json`, so a session whose root has moved to another profile is recovered under its current ID. Nothing else in `.godmode` holds the ID. The session's kind (`ProjectStatus.Kind`, `ProjectSummary.Kind`) is the create script's `kind`, else the action's name, and the app shows it as a label on the session's row and tile.

The folder name comes from the project's name: spaces become underscores, characters that are invalid in a file name are dropped, and so are trailing dots, which Windows drops from a folder name (`foo.` is the folder `foo`). A name that leaves no folder of its own (empty, `.`, `..`, or dots only) is refused before anything is created or run. So is a folder name, reused or returned by a script, that ends in a dot or a space, and a Windows device name (`CON`, `PRN`, `AUX`, `NUL`, `COM0`–`COM9`, `LPT0`–`LPT9`, with or without an extension: `nul.txt`), on every OS, so a root's projects are the same on every host.

A create script's `project_path` must be strictly inside the script's own root: not above it, not in a sibling root or anywhere else, and not the root itself unless its action shares its folder ([A root as its own workspace](#a-root-as-its-own-workspace)). Links are followed where the OS allows, so a link in the root to a folder elsewhere is that folder, and refused.

A root keeps some folders for itself at its top level, and no project may be one of them or inside one, whether named in the create dialog, reused (`__reuseExisting`), or returned as a create script's `project_path` (`{root}/.godmode-root/scripts` is refused): `.godmode-root` (the root's config and scripts), `logs` (its script logs and result files), `.godmode` (the state of the sessions that work in the root itself) and `.archived` (left over from archiving, which is gone). A delete of such a project would delete that folder. They are compared ignoring case and trailing dots and spaces, as Windows compares folder names, on every OS. A refused name creates nothing; a refused `project_path` leaves the create `Error` in the folder it was given.

**A delete removes only a project folder inside a root, never the root.** Before it deletes a project's folder, the server checks it as it checks a `project_path` of an action that does not share its folder: strictly inside a configured root, links followed, not in a folder the root keeps for itself. A folder that is not (its root was removed from the config while the project was tracked, or the folder was replaced by a link) is left on disk; the delete fails saying so, after the delete scripts ran and the project was forgotten.

**`session-id` is only ever a GUID**, which is what the server asks for (`--session-id`) and what claude reports in `system/init`. The session can write the file itself, and the value is the argument after `--resume`, so a saved value that is not a GUID (`--settings=x` would be read as a flag) is logged and treated as no session: the resume starts a fresh session on a new GUID, told to carry on from the work in the folder, as it does when claude has no conversation for the session. A `system/init` reporting a session that is not a GUID is logged and ignored.

## Running the Server

### Development

```bash
dotnet run --project src/GodMode.Server/GodMode.Server.csproj
```

With no config file this is a dev server: it runs on appsettings, with an empty `roots` folder under the working directory, and it leaves the roots of any other server alone (see [One server per root](#one-server-per-root)). Run it beside the server you use on another port, with its own config file or none:

```bash
dotnet run --project src/GodMode.Server/GodMode.Server.csproj -- --Urls=http://127.0.0.1:31338
dotnet run --project src/GodMode.Server/GodMode.Server.csproj -- --config ~/.godmode-server/dev.json --Urls=http://127.0.0.1:31338
```

The server you use gets its own file: `--config <path>` (or `GODMODE_CONFIG`), with its `Instance`, its roots (`Roots:Scan`, `Roots:Explicit`), its `Profiles` and, if you like, its key.

The server builds no React and needs no npm. To see the client, run the GodMode app (on Windows, `dotnet run --project src/GodMode.Maui/GodMode.Maui.csproj -f net10.0-windows10.0.19041.0`) and add the server there with its key.

### Production

```bash
dotnet publish src/GodMode.Server/GodMode.Server.csproj -c Release -o publish
cd publish
./GodMode.Server
```

The machine also needs `claude` and, for root scripts, `pwsh`: on the `PATH`, or named by their settings ([Executables](#executables)). The server itself needs no Node. A repo whose `.mcp.json` starts MCP servers with `npx` needs it, as does one with a JavaScript toolchain.

## SignalR Hub API

### Client → Server Methods

A connection's calls run up to four at a time (`MaximumParallelInvocationsPerClient`), so a `ReplyAndResume` waiting for a resumed claude's session leaves the tab its `StopProject` and its subscribes. One connection's `SubscribeProject` calls still run one at a time, in the order they came.

Projects:
- `Task<ProjectSummary[]> ListProjects()` — Get all projects
- `Task<ProjectStatus> GetStatus(projectId)` — Get project status
- `Task<CreateProjectResult> CreateProject(profileName, projectRootName, actionName, inputs)` — Create a project with form inputs (`actionName` null = default action): its `Project`, or, for an action that starts no session, no project and the script's `Message` ([Actions that start no session](#actions-that-start-no-session))
- `Task SendInput(projectId, input)` — Send input to Claude (while a permission prompt or question waits, it answers that instead)
- `Task RespondToPermission(projectId, requestId, decision)` — Allow or deny the project's `PendingPermission`; fails when the request is not pending, another answer to it came first included
- `Task<PermissionDetail> GetPermissionDetail(projectId, requestId)` — Everything the pending permission request would run, to show before Allow (see [The MCP endpoint](#the-mcp-endpoint))
- `Task AnswerQuestion(projectId, requestId, answers)` — Answer the project's `PendingQuestion` (question text → chosen label or free text)
- `Task StopProject(projectId)` — Stop running project: interrupt claude, then kill its process tree after `StopGracePeriodSeconds` (see [Stopping a Session](#stopping-a-session))
- `Task ResumeProject(projectId)` — Resume stopped project; it is `Idle` until the user writes
- `Task SubscribeProject(projectId, fromOffset, subscriptionId, generation)` — Replay `output.jsonl` from `fromOffset` (the byte offset after the last line the client has; 0 for all, `-N` for the last N turns) in `OutputBatch` messages, then `OutputReplayComplete`, then live `OutputReceived` lines, each line once and in order. `subscriptionId` is the client's own, echoed by this subscription's batches and complete; `generation` is the output generation `fromOffset` is in (null when the client holds none), and a positive offset in any other replays from 0
- `Task UnsubscribeProject(projectId)` — Unsubscribe from output
- `Task<DeleteProjectResult> DeleteProject(projectId, force)` — Stop the project, run delete scripts and remove it; a refused delete leaves it `Stopped`. A session that shares its folder is moved to the folder's trash (`Trashed`), any other loses its working folder
- `Task<ProjectStatus> RestoreProject(projectId)` — Undo a delete that trashed the session: back under the same ID, `Stopped`, pushed as `ProjectCreated`; fails when its root is not listed under its ID's profile and name now, or it is not in the trash (see [The trash, and folding](#the-trash-and-folding))

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
- `ProjectCreated(status)` — New project created, or recovered live from a root that appeared or took another profile (*Live roots*)
- `CreationProgress(projectId, message)` — Script progress during project creation (for an action that starts no session, under the run's own ID, which names no project); it ends when `CreateProject` returns
- `ProjectDeleted(projectId)` — Project deleted, or left the list with its root (*Live roots*)
- `RootsChanged(roots, profiles)` — The whole `ListProjectRoots` and `ListProfiles` lists, pushed only when they differ from the last ones read (*Roots and profiles*, *Live roots*)

### HTTP Endpoints

- `GET /health` — Anonymous liveness probe
- `GET /` — What the server is, with the key: `{"service":"GodMode.Server","version":…,"status":"running"}` (the app's codespace probe reads it). No path serves a page
- `POST /mcp` — GodMode's MCP endpoint, for its sessions' claude, with the project token of its MCP config: see [The MCP endpoint](#the-mcp-endpoint)

## Dependencies

- **.NET 10** — Runtime
- **SignalR** — Real-time communication
- **ModelContextProtocol.AspNetCore** — The MCP endpoint
- **GodMode.Shared** — Shared types and models
- **GodMode.ProjectFiles** — Project folder management

## Troubleshooting

### Server Exits at Startup

- "will not start: … API key file": the key file cannot be written, or is under a scan folder or an explicit root (the message names which). Set `Authentication:ApiKeyFile`, or a key. See *Authentication and binding* above.
- "will not start: PermissionPromptKeepAliveSeconds …": it must be more than 0 and less than 300.
- "will not start: its config file, … does not exist": the file named by `--config` or `GODMODE_CONFIG` is not there.

### Claude Process Not Starting

- The project is `Error`, and its `LastError` says why
- Ensure `claude` command is in PATH
- Check Claude CLI is installed: `claude --version`
- Review logs in `.godmode-logs/` under the working directory

### Roots or Projects Missing

- Check the roots' sources are what you think: the server logs each at startup (`Roots from Roots:Scan:<key>: <folder>`; relative paths resolve against the working directory). And check it was started with its config file (`Config file: …` at startup): user secrets are not read
- A root whose name or folder another root has is skipped: look for "clashes with the root" in the log, which names both paths
- A root another live server holds is skipped: look for "held by another server" in the log, which names the holder's instance and process
- Each root needs a `.godmode-root/` folder directly inside it
- Verify `status.json` files are valid JSON
- Review startup logs

### Scripts Failing

- Check a script with a matching extension exists for your OS (a `.ps1` needs `pwsh` on the `PATH`)
- Check stderr output in the server logs, and the create's own log, `{root}/logs/<id>.log`
- Ensure environment variables are correct

### SignalR Connection Failures

- Every server requires its API key: add the server in the app with it (a codespace, with a GitHub token of its owner)
- A request refused with 403 carried an `Origin`, as a browser's does: the server's log names it. Only the app is a client
- Check firewall rules for port 31337
