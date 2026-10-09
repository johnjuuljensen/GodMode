# GodMode.Server

SignalR server for GodMode. It runs Claude Code sessions in project folders on the machine it runs on and streams their output to its clients, the GodMode app's relay and attention service. It serves no page: the React client is built into the app alone, and no browser is a client.

## Features

- **Real-time Communication**: SignalR hub (`/hubs/projects`) for bidirectional communication
- **Process Management**: Spawn, stop and resume Claude Code processes
- **Config-Driven Project Roots**: Roots from the server's config (scan folders and explicit roots), with per-action config overlays
- **Script-Based Creation**: VCS-agnostic — all prepare/create/delete logic lives in scripts, not server code
- **Cross-Platform Scripts**: Write `.ps1` scripts once; they run under `pwsh` on Windows and Linux
- **State Persistence**: Each session's state lives in its working folder's `.godmode/sessions/<id>/` and is recovered on restart
- **Permission prompts**: Every session asks the user for permission through the server's own MCP endpoint, `/mcp`: claude's `--permission-prompt-tool`, beside `message_parent` and `speak`
- **Messages between sessions**: Sessions in one `CLAUDE_CONFIG_DIR` reach each other through Claude Code's own channel, by the names GodMode gives them; across config dirs, and to a stopped parent, through the server (`message_parent`, notices), held until the receiver can take them
- **Spoken replies**: Every session is asked to give a short spoken version of each reply with `speak`, which the voice says word for word ([A session's spoken reply](#a-sessions-spoken-reply))
- **The fleet**: An overseer lists, starts, messages, reads, stops and resumes sessions with the tools of `/mcp/fleet`: the user's own claude with the server's credential, or a GodMode session its root's config gives them

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
| `Roots:Explicit:<name>:Title` | The explicit root's title, when its own `config.json` has no `title` |
| `Profiles:<name>:Description` | The profile's description, as the app shows it |
| `Profiles:<name>:Environment:<VAR>` = value | An environment variable of every session and root script in the profile (a `CLAUDE_CONFIG_DIR`, a service's token) |

- **A root's profile** is its `config.json`'s `profileName`, else its explicit entry's `Profile`, else `Default`. A profile is listed when it has a root; one named under `Profiles` alone is only settings.
- **A root's title** is its `config.json`'s `title`, else its explicit entry's `Title`, else none, and the app and voice show its name. It is display only: the name stays the root's key, in session IDs, `CreateProject`, the fleet's `start_session`, links and logs, so two profiles' roots keyed `Outbound-Assistant` and `Mega-Assistant` can both be titled `Assistant`. A title may be another root's name. The app shows the name as the title's tooltip, and beside the title when two roots in one list would show one title; voice takes either, and tells two profiles' roots of one title apart by the profile. `ListProjectRoots` (and `RootsChanged`, and the fleet's `list_roots`) gives it as `Title`; a session's status carries only its `RootName`, and clients look the title up in the roots list, which is pushed again when a title is edited.
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
- **A root's `profileName` edited, or its explicit entry renamed**: its sessions' IDs (`{profile}/{root}/{id}`) name the profile and the root, so each session takes the ID the root has now, as a restart would give it. One without a claude does so at once: its old ID is pushed as `ProjectDeleted`, its new one as `ProjectCreated`, and its `status.json` is rewritten, and so are its children's, to name its new ID ([A session's parent](#a-sessions-parent)). One whose claude runs keeps its ID, which its MCP config carries, until claude exits, then does the same.
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
| Scalars (description, nameTemplate, model, effort, permissionMode, allowSkipPermissions, resumeOnRestart, resumePrompt, etc.) | Overlay replaces if present |
| `environment` | Dictionary merge, overlay keys override |
| `claudeArgs` | Concatenated (base + overlay) |
| Script fields (prepare, create, delete, status) | Overlay replaces entirely |

`profileName`, `title` and `stripEnvVarProfile` are read from `config.json` only.

### Action Discovery

- Scan `config.*.json` → action names from filenames
- If only `config.json` exists (no `config.*.json`) → single default "Create" action
- If no config exists at all → default form with name + prompt fields

### Fields

| Field | Description |
|-------|-------------|
| `description` | Shown in the UI when selecting an action |
| `profileName` | Profile the root belongs to (`config.json` only). Default: `Default` |
| `title` | What the app and voice show for the root (`config.json` only); its name stays its key. Default: none, the name is shown |
| `environment` | Env vars set for scripts and passed to Claude processes, on top of the few they inherit (see [Environment](#environment)). Values support `${VAR}` expansion from the server's environment |
| `prepare` | Scripts run before project folder is created (working dir = root) |
| `create` | Scripts run to create the project (working dir = project, or root if `scriptsCreateFolder`) |
| `delete` | Scripts run when a project is deleted (working dir = root) |
| `status` | One script that reports the project's pull request (working dir = project): see [Pull request status](#pull-request-status) |
| `list` | One script (`config.json` only; an overlay's is ignored) that prints the root's folders to offer for adopting (working dir = root). Without it, the root's immediate subfolders are offered. See [Adopting folders](#adopting-folders) |
| `adopt` | Whether the action's `create` script knows how to adopt a folder that exists: an adopt with the action runs it, alone, with `GODMODE_ADOPT=true`. Default `false`: an adopt with the action runs no script. See [Adopting folders](#adopting-folders) |
| `claudeArgs` | Extra CLI arguments appended when starting Claude |
| `model` | Default `--model` for the action: an alias (`fable`, `opus`, `sonnet`, `haiku`) or a full name (`claude-fable-5`). A `model` form input overrides it. Kept with each project at create (`status.json`), so its resumes keep it. Passed as given, unchecked |
| `effort` | Default `--effort` for the action: `low`, `medium`, `high`, `xhigh` or `max`, any case. An `effort` form input overrides it, and an empty one passes none (claude's own default) over the action's. Kept with each project at create (`status.json`), so its resumes keep it. Any other value is refused: in a config file, as an error in that file, as an unknown `permissionMode` is; in a create or adopt input, before anything is created. Default: none |
| `permissionMode` | claude's `--permission-mode` for the action's projects: `acceptEdits`, `auto`, `manual`, `dontAsk` or `plan`. Kept with each project at create. Default: none (claude's own settings decide). See [Permissions](#permissions) |
| `allowSkipPermissions` | Whether the action's projects may run with `--dangerously-skip-permissions`. Default `false`. See [Permissions](#permissions) |
| `fleetTools` | Whether the action's sessions get the fleet's tools: `true` (every one), `"grantable"` (one a session with them starts with `fleet_tools: true`), or `false`. Any other value is a config error. Read on every call of a fleet tool. Default `false`. See [Overseer sessions](#overseer-sessions) |
| `quietTurns` | Whether the action's sessions end their turns quietly: a turn the user did not start (one woken by a worker's message, a notice, or a background task of its own) raises no `Finished`; one the user started, with `SendInput`, `ReplyAndResume` or `AnswerQuestion`, does. The result still shows as the session's last. For an overseer's actions (`overseer`, `epic`). Read at each launch. Default `false`. See [Quiet turns and escalation](#quiet-turns-and-escalation) |
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

GodMode gives a session one MCP server, its own: `godmode`, this server's `/mcp` endpoint, in the `--mcp-config` file it launches claude with (see [The MCP endpoint](#the-mcp-endpoint)). A session with the fleet's tools gets a second, `godmode-fleet`, this server's `/mcp/fleet` (see [Overseer sessions](#overseer-sessions)). It configures no others:

- A repo brings its MCP servers in its own `.mcp.json` (Claude Code's project scope).
- User-scoped servers live in the profile's Claude config: the `CLAUDE_CONFIG_DIR` its `environment` (or the root's) sets, for example with `claude mcp add --scope user` run with that `CLAUDE_CONFIG_DIR`.

A root or action config that still has `mcpServers`, or a profile with an `mcp/` folder, launches normally: the server logs a warning once for each, and ignores it.

GodMode pre-approves only its own session tools, `message_parent` and `speak` on `/mcp`: every launch passes `--allowedTools mcp__godmode__message_parent mcp__godmode__speak`, in any root and permission mode (#384). A root's own `--allowedTools` in `claudeArgs` (`--allowedTools a b`, `--allowed-tools`, or `--allowedTools=a`) gets the two added to its list, the first one it has, so claude is given one list; with none, the server adds the flag after the root's args. Any other tool call that needs approval, an MCP tool's included (the fleet's too), reaches the permission prompt (`WaitingPermission`), unless Claude Code's own settings allow it (`permissions.allow` in the profile's `CLAUDE_CONFIG_DIR`, or the repo's `.claude/settings.json`), the root's `permissionMode` lets it through, or the project runs with skip-permissions (see [Permissions](#permissions)).

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

`/mcp` serves MCP over streamable HTTP, statelessly, with three tools: `permission_prompt`, which claude calls by itself, `message_parent`, which the model calls to message its parent ([Messages between sessions](#messages-between-sessions)), and `speak`, which the model calls on every turn with the spoken version of its reply ([A session's spoken reply](#a-sessions-spoken-reply)). Its `initialize` gives the server's instructions, which ask for `speak`; `/mcp/fleet` gives none. claude is launched with `--permission-prompts host --permission-prompt-tool mcp__godmode__permission_prompt` and an MCP config whose entry, its only one unless the session has the fleet's tools ([Overseer sessions](#overseer-sessions)), is:

```json
{ "mcpServers": { "godmode": {
  "type": "http",
  "url": "http://127.0.0.1:31337/mcp",
  "headers": { "Authorization": "Bearer <project token>", "X-GodMode-Project-Id": "<project ID>" }
} } }
```

- **The URL** is an address this machine reaches the server on, from the addresses it is bound to: a loopback binding first, a wildcard's `127.0.0.1` next, else the one IP bound.
- **The token** is issued afresh for each launch and lives only in memory and in this file, until the process exits: then the server forgets it, so a token read from a leftover file opens nothing. The file is `{root}/logs/<id>.mcp-config.json`, beside the session's record, out of its working folder: a neighbour in a shared folder could otherwise take the token and speak as the session (`message_parent`, and the fleet's tools when it has them). It is owner-only where the OS allows, and deleted when the process exits. No environment variable carries it.
- **Only a project token opens `/mcp`**, and only for the project it was issued to. The user's API key does not, and a project token opens nothing else: not the hub, not `/api/*`, and `/mcp/fleet` only while its session has the fleet's tools.
- **The permission prompt** takes claude's flat arguments, `tool_name`, `input` (an object) and `tool_use_id` (optional). It waits until the user answers, however long that takes, and returns claude `{"behavior":"allow","updatedInput":{…}}` or `{"behavior":"deny","message":"…"}` as text.
- **While it waits,** it sends a progress notification every `PermissionPromptKeepAliveSeconds` (default 30). claude gives up on a tool call that sends no response or progress for 300 seconds.
- **When claude cancels the call**, or its connection drops, the request is withdrawn (denied).
- **What clients see** is `ProjectStatus.PendingPermission`: the tool's name and a one-line `Summary` (`Bash: git push origin x`, ending with ` …` when it leaves something out), which a notification shows and speech reads. It carries no tool input, and neither does `status.json`: a `Write` can be megabytes, and the request is pushed in every `StatusChanged`, `ListProjects` and `AttentionChanged`. The input stays in the server's memory for as long as the call waits, which is as long as it can be answered: a restart ends the call, and claude asks again with its input.
- **Before Allow**, a client fetches `GetPermissionDetail`: the whole command for `Bash` and `PowerShell`, the path and the whole new text for `Write` and `NotebookEdit`, the path and each replacement for `Edit` and `MultiEdit` (`Replace:`, or `Replace every occurrence of:` with `replace_all`, the old text, `With:`, the new text), and the input as indented JSON for any other tool, cut at 16384 characters with `DetailTruncated` set. The call runs with all of it.
- **One answer counts.** When two clients answer at once, the one that came second fails, as does any answer to a request that was answered already or withdrawn: claude got the other.

### The fleet endpoint

`/mcp/fleet` serves MCP over streamable HTTP, statelessly, for an overseer, which lists, starts, messages, reads, stops and resumes the server's sessions: a `claude` the user runs themselves, in a terminal, outside GodMode, or a GodMode session with the fleet's tools ([Overseer sessions](#overseer-sessions)). The sessions it starts are GodMode sessions like any other: in the app's list, notifying, and asking the user for their permissions.

- **The server's own credential opens it**, as it opens the hub: the API key (`Authorization: Bearer <key>`), or in codespace mode a GitHub token of `GITHUB_USER`. So does the project token of a session that has the fleet's tools, with its project named, checked on every request. A session's token with its own project named, when the session has no fleet tools, gets 403; a project token with no project named, or with another project named, opens nothing (401). The API key does not open `/mcp`. `/mcp/fleet/`, with a trailing slash, is the same endpoint, as routing has it. A request with an `Origin` gets 403, as everywhere.
- **Each endpoint lists its own tools.** `/mcp` has `permission_prompt`, `message_parent` and `speak`, `/mcp/fleet` the tools below, whoever calls. One MCP server serves both; each tool's `[Authorize]` policy is its endpoint's, by the request's path too, so a granted session's token gets no permission prompt on `/mcp/fleet` and no fleet tool on `/mcp`.
- **The overseer's `.mcp.json`** (in the folder it runs in, or `claude mcp add --transport http godmode-fleet <url> --header "Authorization: Bearer <key>"`):

  ```json
  { "mcpServers": { "godmode-fleet": {
    "type": "http",
    "url": "http://127.0.0.1:31337/mcp/fleet",
    "headers": { "Authorization": "Bearer ${GODMODE_API_KEY}" }
  } } }
  ```

  Use the `${GODMODE_API_KEY}` form: Claude Code expands `${VAR}` in `.mcp.json`, so the key stays in the environment of the shell that runs the overseer, not in the file. Never put the key itself in a repo's `.mcp.json`, nor add the server with `claude mcp add … --scope project`, which writes the header as given into the repo's `.mcp.json`: the key would be committed with it. A codespace's URL is `https://<codespace-name>-31337.app.github.dev/mcp/fleet`, with a GitHub token of the codespace's user (`gh auth token`).
- **Tools.** Each returns JSON text, PascalCase as the hub's models; a refusal is the tool's error, with the reason the hub would give.
  - `list_sessions` — every session: `Id`, `Name`, `Address` (its name in Claude Code's own channel, `SendMessage`'s `to` from a session in its config dir: [Messages between sessions](#messages-between-sessions)), `Profile`, `Root`, `Kind`, `Action`, `State`, `ParentId`, `Needs` (its attention item's kind: `Permission`, `Question`, `Error`, `Escalation`, `Review`, `Finished`; absent when nothing; a child's `Finished` and `Review` too, which the user's list leaves out), `PullRequestUrl` and `BackgroundTasks` (what its claude runs in the background, absent for nothing: a session `Idle` with them is still working; [Background tasks](#background-tasks)).
  - `list_roots` — `Profiles`, and `Roots` with their `Name` (the key `start_session` takes), `Title` (display only, when it has one) and `Actions` as `ListProjectRoots` gives them: each action's `InputSchema`, `Model`, `Effort` and whether it starts a `Session`.
  - `start_session(profile, root, action?, inputs?, model?, effort?, parent?, top_level?, fleet_tools?)` — `CreateProject`'s path: `inputs` by the action's schema, `model` and `effort` over the action's (an unknown effort is refused). With `parent` the session is that session's child ([A session's parent](#a-sessions-parent)). Without it, a session calling is the new one's parent, unless `top_level` is true; the server's credential's are top level. `fleet_tools: true` grants the new session the fleet's tools, where its action allows a grant ([Overseer sessions](#overseer-sessions)). It is pushed to the app as `ProjectCreated`, and returns its `Id`, `Name`, `Address`, `State`, `Kind`, `ParentId`, `Model` and `Effort`; an action that starts no session returns its script's `Message`. Refused: a `skipPermissions` input that is true (the session's prompts are the user's; false, the schema's default, is fine), a `__parentId` input (`parent` names the parent), `parent` with `top_level`, and `fleet_tools` for an action whose `fleetTools` is `false`, or that starts no session.
  - `send(session, text)` — `ReplyAndResume`: to a running claude, or a resume with it. **Held while the session waits on the user** (a permission prompt, an AskUserQuestion, a question it ended its turn on or was stopped on), labelled with its sender, and delivered once the user has answered and the turn has ended: it never answers what the user is asked. Held too behind messages already held, so it does not overtake them ([Messages between sessions](#messages-between-sessions)). At most 8000 characters, and refused while what is held for the session is full. Returns its `State`, and `Held`, why, when it was held.
  - `read(session, turns?)` — `Address`, `State`, `Kind`, `ParentId`, `Model`, `Effort`, `PullRequestUrl`, `WaitingOn` (what it needs, in full: a permission's `Tool`, summary and `Detail`, everything the call would run, as `GetPermissionDetail` gives it; a question's text and its `Question` with options; the whole error; a pull request's review; a finished turn's whole result), `BackgroundTasks` (each with its last `Step`), and `Replies`, its last `turns` (default 1, at most 20) replies, oldest first ([A session's last replies](#a-sessions-last-replies)).
  - `stop(session)`, `resume(session)` — `StopProject` and `ResumeProject`. Return the `State`.
  - `escalate(text, url?)` — asks the user to decide something: an `Escalation` item on the calling session, `url` (http or https) its `PullRequestUrl` ([Quiet turns and escalation](#quiet-turns-and-escalation)). A session alone: the server's credential is refused. At most 8000 characters. Returns the caller's `State`.
- **The overseer drives what it starts and what it sends to.** `send` and `resume` work on any session, one the user started with skip-permissions included, which asks the user nothing. An overseer that reads untrusted text (an issue's, a pull request's, a reply that quotes one) and acts on it is a prompt-injection path into those sessions: point it at work and sessions you would run unattended.
- **Not provided:** answering a permission or a question, delete, forget, adopt, restore, or anything that writes config. Merging stays in the overseer's own `gh`: the server is VCS-agnostic.

### Overseer sessions

A GodMode session can be an overseer: it gets the fleet's tools in its own MCP config, and the sessions it starts are its children. A session has none by default.

- **The root's config grants them**, per action, as `allowSkipPermissions` allows skip: `"fleetTools": true` gives every session of the action the tools; `"grantable"` gives them to one whose starter, a caller with the tools (a session that has them, or the server's credential: the user), asked with `start_session(..., fleet_tools: true)`; `false` (the default) to none. An action the user starts overseers from in the app is `true`: the repo's godmode-dev root gives its `overseer` and `epic` actions `true`, so an epic overseer has the tools however it was started. `"grantable"` is for an action whose sessions have them only when an overseer grants them.
- **Nothing in the session's working folder grants them.** Its `settings.json` is in its working folder, and names its action, so it is not read for the grant: a session that writes `"fleetTools": true` there, or renames its action to an overseer's, gains nothing, at once or after a restart. What the session was started as is the server's own record, `{root}/logs/{id}.fleet` (its action, whether its starter granted the tools, its working folder, relative to the root, and its `Parent`, the session that started it ([Messages between sessions](#messages-between-sessions))), written by its create or adopt beside its create log; a session without one (made before it was kept) has no grant. The record is the session's of that folder alone: a session's id is its state folder's name, which any session can make in its own folder, so one recovered under the id from another folder has no grant. A delete or a forget deletes the record (and the trash's purge does, for any left), so a session restored from the trash has no grant: the fail-safe choice, since a neighbour in a shared folder could plant the trashed id in that very folder. Restored, an overseer needs starting anew. A root whose working folder is the root itself ([A root as its own workspace](#a-root-as-its-own-workspace)) has its `.godmode-root` and `logs` in that folder, so its sessions could edit the config that grants them: give such a root no `fleetTools`.
- **Checked on every call**, against the root's config as it is then, as each launch checks skip: a call of a fleet tool with the session's token, the listing included, is refused (403) once the action no longer grants them, or the config cannot be read, with no relaunch. Each launch lists `godmode-fleet` in the MCP config only when the session has them then.
- **The MCP config** of a session with the tools has a second entry, with the same headers (its project and its launch's token) as `godmode`'s:

  ```json
  "godmode-fleet": {
    "type": "http",
    "url": "http://127.0.0.1:31337/mcp/fleet",
    "headers": { "Authorization": "Bearer <project token>", "X-GodMode-Project-Id": "<project ID>" }
  }
  ```

  Its tools are `mcp__godmode-fleet__list_sessions` and the rest. GodMode pre-approves none: each call reaches the permission prompt unless the session's permission mode or Claude Code's settings allow it ([MCP Servers](#mcp-servers)), for example `"permissions": { "allow": ["mcp__godmode-fleet"] }` in the repo's `.claude/settings.json`, which allows the fleet's tools and not the permission prompt's server.
- **Why `/mcp/fleet`, not `/mcp`.** One tool set, with one policy and one grant check, for both kinds of overseer, and each endpoint keeps one job: `/mcp` is claude's own (the permission prompt it calls by itself), `/mcp/fleet` the tools a model calls. A session without the grant has no fleet entry in its config at all, rather than an endpoint that hides tools from it, and an allow rule can name the fleet's server without naming the permission prompt's.
- **Its children are its own.** A session it starts has it as parent, unless it passes `top_level: true` or names another `parent`, and gets no tools of its own unless granted them.
- **Its own profile alone, and its links.** A profile is another account, with its own secrets, and GodMode never mixes profiles in a list, so a session's fleet tools see its profile only, and the roots a `Fleet:Links` entry links its root to ([Messages between sessions](#messages-between-sessions)): `list_sessions` and `list_roots` list those; `send`, `read`, `stop` and `resume` on another profile's session are refused as an unknown ID is, so a session cannot learn it exists; `start_session` into another profile's root, or under another profile's session as `parent`, is refused, naming the missing link. Its children are in its own root, or one a link lets it oversee: `start_session` as parent into another root is refused without a link. The server's credential, the user's own overseer, sees every profile, unscoped by links.
- **Its MCP config is out of its working folder:** `{root}/logs/<id>.mcp-config.json`, beside its record, as every session's is. A session in a shared folder ([Several sessions in one folder](#several-sessions-in-one-folder)) has its neighbours in that folder, and its token opens the fleet's tools.
- **An overseer never answers a permission prompt or a question**, its children's or anyone's: no tool does, and `send` is held while one waits. A child's prompts go to the user, through the inbox and notifications.
- **It hears from its children without polling** ([Messages between sessions](#messages-between-sessions)). In its children's config dir: their `SendMessage`, and `notify_when_idle`, which it arms on each child it dispatches. In another: their `message_parent`, and the server's notices. Each wakes it, idle, as a new turn. A stop, a restart or a resume ends its `notify_when_idle` subscriptions, so a resumed overseer surveys with `list_sessions` and arms them again. Each woken turn ends with a `result`, which is the overseer's `Finished` attention item, unless its action has `quietTurns` ([Quiet turns and escalation](#quiet-turns-and-escalation)). claude also starts a turn of its own when a background task it started finishes (Bash `run_in_background`, `Monitor`, `ScheduleWakeup`): measured with claude 2.1.287, a session that ran `sleep 30` in the background woke 40 seconds later.
- **An overseer action** (the repo's godmode-dev root, `.devcontainer/godmode-server/roots/godmode-dev/`): `overseer`, a coordinator, `"fleetTools": true` and `"sharedFolder": true`, in one folder every coordinator shares (`{root}/overseer`, a detached worktree of `origin/master`, for `gh` and the skills, edited by none); and `epic`, an epic's overseer, `"fleetTools": true`, in the worktree of its branch `epic/<n>-<slug>`, made or checked out and pushed. Both allow the fleet's tools with `"--allowedTools", "mcp__godmode-fleet"` in `claudeArgs`, to which every launch adds `message_parent` and `speak` ([MCP Servers](#mcp-servers)). Both say `"allowSkipPermissions": false`, so no session with the fleet's tools runs with permissions skipped. The `issue` action they dispatch with takes `baseBranch` and a `brief` added to its prompt. Their create scripts write the prompt (`project_prompt`): a `promptTemplate` with an input's `{placeholder}` falls back to the `prompt` input when that input is missing or its text has braces. Give both `"quietTurns": true` in the host's root config (`config.overseer.json`, `config.epic.json`): it is the root's choice, off unless a root turns it on, and the godmode-dev root on a host is its owner's config.

### Quiet turns and escalation

An overseer runs many turns the user did not start: its workers' messages, the server's notices, its own background reviewers finishing. Measured on one overseer (2026-10-02, issue #401): 37 turns in 2.5 hours, 2 of them the user's, and each raised or refreshed a `Finished` item, with a notification. Three things keep the fleet's routine out of the user's inbox, and what the user must decide in it.

- **A child's `Finished` and `Review` are its parent's.** `GetAttention` (and so `AttentionChanged`) leaves out the `Finished` and `Review` items of a session with a parent in the server's record (`{root}/logs/{id}.fleet`, not the session's own `ParentId`): the overseer hears of them and reports on the work. Its `Permission`, `Question`, `Error` and `Escalation` are the user's, as before. The fleet's `list_sessions` and `read` keep the full view. Each `AttentionItem` and `ProjectSummary` carries `RecordedParentId`, that parent, so a client can leave a child's items to its overseer too (voice, #469).
- **Quiet turns.** A session whose action has `"quietTurns": true` raises no `Finished` at the end of a turn the user did not start. The server cannot tell a turn woken by `SendMessage` or a background task from the user's own by its origin, so the setting is the action's, and the user's turns are the exception: a turn that took the user's `SendInput`, `ReplyAndResume` or `AnswerQuestion` raises `Finished` as any session's does. The fleet's `send`, a held message or notice, and the create's prompt are not the user's. A quiet turn's result is still the session's `LastResult`, with `ProjectStatus.QuietResult` true; a `Finished` the user had not seen when quiet turns came after it stays listed, with its own result (`ProjectStatus.UnseenResult`), until it is seen. A plain-text question at a quiet turn's end is still a `Question`: an overseer ends no turn on one. The decision is one place, `StatusUpdater.IsQuietTurnEnd`, for a turn outcome the session declares (#467) to extend.
- **Escalation.** With quiet turns, a decision the overseer finds in a woken turn would go unseen. The fleet's `escalate(text, url?)` raises an `Escalation` item on the calling session: it stays through every turn after it, and a restart, until the user sees it (`MarkSeen`, or the user's own `SendInput`, `ReplyAndResume` or `AnswerQuestion`; the fleet's `send` and a restart's resume leave it), and a second `escalate` replaces it. GitHub assignment stays the record; the item is the nudge. It ranks after an `Error` and before `Review` and `Finished`.

### Messages between sessions

A session and its parent talk two ways: through Claude Code's own cross-session channel, when they share a `CLAUDE_CONFIG_DIR`, and through the server otherwise. Neither answers a permission prompt or a question: those stay the user's.

**Claude Code's own channel.** It is the default, used whenever it can be.
- **Who can use it.** A `--print` session registers in its `CLAUDE_CONFIG_DIR` (`sessions/<pid>.json`, with a message pipe of its own), and `ListAgents` lists it to every session registered in the same dir. `SendMessage` to it arrives as a new user turn, a `<cross-session-message from-name="…">` block, which wakes an idle session. `notify_when_idle` tells the subscriber, with a turn of its own, when the session ends its next turn.
- **It knows no roots, profiles or links.** Every session in one config dir can reach every other, across roots and profiles, without a `Fleet:Links` entry: links gate only the server's relay. Profiles that must not talk give their roots separate config dirs.
- **Its name is its address.** GodMode names every launch itself, resume included, with `-n <address>`.
  - The address is `{root}-{id}`: the root's name, then the session's id. Anything but a letter, a digit, `.`, `_` or `-` in the root's name becomes `-`. A name that changed so ends with `_` and 6 hex digits of its hash, so `a b` and `a-b` stay apart.
  - It is unique on the server, since one name is one root. It is stable while the root keeps its name. A re-key (the root renamed, or moved to another profile) gives the session a new address, under a claude that still runs with the old one until its next launch. `message_parent` does not depend on it.
  - **It is a name, not an identity.** Any process in the config dir could start `claude -n <address>`. Only the server's relay checks who a message is from.
  - A root's own `-n` or `--name` in `claudeArgs` is taken out, so GodMode's name wins. The server logs a warning once for that session.
  - `list_sessions`, `read` and `start_session` give each session's `Address`.
  - A session's claude and its scripts get its own address as `GODMODE_SESSION_ADDRESS`. A child's claude and its prepare and create scripts get its parent's as `GODMODE_PARENT_ADDRESS`, beside `GODMODE_PARENT_ID`, for the child's prompt.
- **Permission modes.** Claude Code holds a message for its user's approval when the sender and the receiver are in different permission classes: one runs with permissions skipped (`bypassPermissions`), the other prompts (`manual`, `auto`, `acceptEdits`, `dontAsk`). Measured with claude 2.1.287: `auto` ↔ `acceptEdits` passed with no hold; a skip-permissions session's message to an `auto` one was held.
  - **A `--print` session can show no approval,** so a held message expires, after 5 minutes by default.
  - **The fleet does not meet the hold:** the fleet's `start_session` refuses `skipPermissions`, so an overseer never starts a skip-permissions worker.
  - **A skip-permissions session** that the user started under a parent reports with `message_parent`, which no hold applies to.
  - **GodMode leaves Claude Code's hold in place.** `crossSessionInbound: accept` would let every prompting session in the config dir (the user's own interactive ones, and every other root that shares the dir) drive a skip-permissions session without approval.
- **What it does not do:**
  - a message to a stopped session is refused ("No agent named … is reachable") and not kept;
  - it does not cross config dirs.

  Those are the server's.
- **What it does to a turn:** a message to a session that is working, or waiting on a permission prompt, is taken at claude's next step, after the prompt is answered, and folded into that turn. It does not interrupt the turn, and it does not answer the prompt.

**Through the server.**
- **`message_parent(text)`** is on `/mcp`, GodMode's own MCP server, which every session has. A session messages its parent with it. The parent gets it labelled `[Message from session <ID> "<name>"]` on a line of its own, then the text. A line of the text that starts like a label (`[Message from…`, `[GodMode notice]…`) is quoted with `> `, so a message cannot pass for the user's words, or for another sender's.
  - **The parent is the server's record**, the `Parent` of `{root}/logs/{id}.fleet` that the create wrote ([Overseer sessions](#overseer-sessions)). A re-key of the parent rewrites it. The `ParentId` in the session's `status.json`, which the session can write, only nests it in the app. A session without a record (made before it was kept) has no parent to message.
  - It refuses:
    - a session with no recorded parent;
    - a parent the server no longer has;
    - a parent across a root or profile boundary without a link;
    - an empty text, and one over 8000 characters;
    - a receiver that already has 50 messages, or 64000 characters, held for it.

    Each refusal says why.
  - Its result is `{"Delivered":true}`, or `{"Delivered":false,"Held":"<why>"}`.
  - It never asks the user: every launch allows it, and `speak`, with `--allowedTools` ([MCP Servers](#mcp-servers)), whatever the root's config or permission mode, so a session reports without asking.
- **The fleet's `send`** to a session waiting on the user is held the same way, labelled with its sender: the calling session, or `[Message from the overseer on the fleet's endpoint]` for the server's credential.
  - Waiting on the user means a permission prompt, an AskUserQuestion, a question it ended its turn on, or a question it was stopped on, a restart included.
  - Its result's `Held` says why.
  - A `send` to a session with messages already held is held behind them, so it does not overtake them, and they are delivered, or resumed with, in order.
  - Otherwise `send` is a reply, as before.
  - The same limits apply to every `send`: 8000 characters, and what is held.
- **Notices.** When a session ends a turn (`Idle`), waits on the user (`WaitingInput`, `WaitingPermission`), fails (`Error`) or stops (`Stopped`), the server tells its recorded parent in one line, for example `[GodMode notice] Session <ID> "<name>" is WaitingPermission, waiting on the user's permission for Bash: Bash: git push; pull request <url>.` It tells only a parent that:
  - has the fleet's tools at that moment;
  - has a running claude;
  - is in the session's root, or in one a link lets it oversee.

  **A parent in the session's `CLAUDE_CONFIG_DIR` gets no notice of `Idle`:** its `notify_when_idle` covers that, and a notice would wake it twice. It still gets the rest. The config dir is that of each session's last launch on this server. Two sessions neither of which has launched since the server started count as the same; one known and one not count as different.
- **Delivery.** Messages and notices alike are held until the receiver can take input: its claude runs, it is `Idle`, and it waits on no permission prompt or question.
  - While it is `Running`, `WaitingInput`, `WaitingPermission` or in `Error`, they are held. **A parent in `WaitingInput` or `Error` holds every relay message and notice until the user replies to it.**
  - When it can take input, everything held goes as one message: the messages first, oldest first, each under its label, then the notices, one line each.
  - A child's later notice replaces its earlier one, so each child has one notice, its latest state.
  - **Each sender is checked again at delivery,** and what no longer holds is dropped and logged:
    - a child's message holds while the receiver is still its recorded parent, in its root or through a link;
    - a session's `send` holds while the sender still has the fleet's tools and sees the receiver;
    - a notice holds while the receiver still has the fleet's tools and the child is still its own.

    So removing a link, or revoking the tools, also clears what was held under them.
  - The input is sent under the receiver's state lock, after the check, so a permission prompt or a turn that starts after the check comes after it. Nothing is sent as a reply: it never answers what is pending, and it marks the result unseen.
- **Where they are held.**
  - **Messages are on disk, out of every working folder:** `{root}/logs/{id}.inbox.jsonl`, beside the session's record, so no session (a neighbour in a shared folder, one working in its root) can write another's.
  - The file is one JSON object per line (`At`, `From`, the sender's ID or null for the server's credential, `Kind`, `Message` or `Send`, and `Text`), oldest first. It keeps no label: the label is made at delivery from `From`. A torn last line is ended before the next append. A delivery takes what it dealt with off the front. A delete, a forget or the trash's purge deletes the file with the record.
  - So a message to a stopped receiver is never lost, across a restart too. It goes with the receiver's next resume, or with its next reply, after the user's words. A resume of a session stopped on a question keeps it for the user's answer. A message does not resume a stopped session by itself.
  - **Notices are in memory,** and a receiver whose claude does not run loses its own. `list_sessions` shows the same when it is resumed.

**Across roots and profiles: `Fleet:Links`.** A parent link stays in one root by default. To cross a root, or a profile, the server's instance config needs a link. It is kept by hand on the host, never in a root's `.godmode-root`, which a session working in its root can write:

```json
{ "Fleet": { "Links": {
  "godmode-to-assistants": { "From": "Godmode/godmode-dev", "To": "Private/*" }
} } }
```

- Each end is `<profile>/<root>`, or `<profile>/*` for every root of the profile. An entry with an end of any other shape is left out, and logged once. Links are read on every check, so an edit or a removal holds from the next call.
- **A link from `From` to `To`** lets a session in `From`:
  - start sessions in `To` with `start_session`, as their parent;
  - see the sessions and roots there with its fleet tools.
  
  It lets those children `message_parent` back, and their parent hear of them. Nothing else crosses: a session's fleet tools still see its own profile ([Overseer sessions](#overseer-sessions)).
- **Without a link:**
  - a session's `start_session` into another profile is refused;
  - so is one as parent into another root of its profile;
  - a `message_parent` to a parent across the boundary is refused, for example after the link was removed;
  - notices do not cross.
  
  Each refusal names the missing link.
- **The server's credential is not scoped by links,** as before. A child it starts under a parent in another root, and a child the app creates with `__parentId` there, still need a link to `message_parent` that parent.

### Slash commands

claude runs a message that starts with `/name` as its command `name`, and takes a `/word` it does not know (`/frobnicate`, `/tmp/x is full`) as text. GodMode sends a short list of them, and refuses claude's others, which never reach claude. Every message that reaches claude is checked: `SendInput`, `ReplyAndResume` (the app), `ReplyByVoice` (voice), the fleet's `send` and a create's prompt (refused before any script runs).

An answer sent by voice (`ReplyByVoice`, #460) is `ReplyAndResume` with the line `[via voice, transcribed]` before it, which the server adds (`SpokenInput`), so the session reads it knowing words may be misheard; a command goes as it is. A typed reply is never marked.

| Input | What GodMode does |
|---|---|
| `/clear` | Sent. claude starts a new conversation (`conversation_reset`, then a new session ID in a new `system/init`, which `session-id` follows). The output starts over: `output.jsonl` is kept as `output-{generation}.jsonl`, a new `output-generation` starts with the reset line, and the clients that follow it live get `OutputRestarted`. The last result and question go with the conversation, and its result, with no text, raises no `Finished`. |
| `/compact [instructions]` | Sent. claude writes `system/compact_boundary` (the app's marker), the summary as a user message with `isSynthetic`, and a result with no text, which keeps the last reply and raises no `Finished`. |
| `/context` | Sent. Its table is the turn's reply. |
| `/recap` | Sent. claude writes a synthetic assistant line and a result with `num_turns` 0 and the recap as its text, which becomes the session's `Recap` and `RecapAt` (as a `speak` call's `recap` does): the last result, its spoken reply and outcome stay, the session is Idle again, and nothing needs the user for it. The hub's `AskForRecap(projectId)` (#513), which voice calls when the user asks about a session, sends it only to a session with no recap whose claude runs idle (nothing pending, no question), once for as long as the server tracks it, as no turn of the user's and no seeing of its last result; it answers `sent`, `asked` (before), `has-recap` or `busy`, and sends nothing for the last three. |
| `/<skill>` | Sent, for every skill the session's last `system/init` listed (`skills`, plugin skills as `plugin:skill`). Before its first `system/init` a skill is taken for text, which claude runs all the same. |
| `/model`, `/effort` | Refused: GodMode sets them per root and action, kept with the project, at each launch, which would undo one sent mid-session. |
| `/rename` | Refused: GodMode names the launch (`-n {root}-{id}`), and `SendMessage` reaches sessions by that name. |
| Any other of claude's commands | Refused: the ones claude 2.1 lists, and any the session's last `system/init` listed in `slash_commands`. |
| Any other text | Sent, as it is. |

The status carries what the last `system/init` listed: `SlashCommands`, what GodMode sends (the three and the skills, which the app's composer completes), and `ClaudeCommands`, all of claude's. A refusal is an `InvalidOperationException` (a `HubException` to the app) that says why. A command sent with a resume carries none of the messages held for the session: they wait for its turn to end.

### A session's last replies

What a session said, whether or not it needs the user, read from its `output.jsonl`. On the server it is `IProjectManager.LastRepliesAsync(projectId, turns)` (`OutputLog.LastRepliesAsync` on a state folder), which gives `AssistantReply(Text, Finished, IsError)`s (`GodMode.Shared`), oldest first. It is the one read behind both of its callers: the fleet's `read`, and the hub's `GetLastReplies(projectId, turns)`, which the voice's `read_reply` calls (#378). Each takes 1 to 20 turns (`IProjectHub.MaxReplyTurns` on the hub) and refuses any other count, and a project the server does not track; neither marks anything seen.

- **A turn ends with a `result` line.** Its reply is its last assistant message that has text (the text blocks joined by a blank line), which is claude's reply as its result repeats it; a turn with none (tool calls only) has the result's text, maybe empty. `IsError` is the result's `is_error`.
- **A subagent's messages are not the session's.** An assistant line with a `parent_tool_use_id` (a Task call's subagent, which the app nests under that call) is skipped.
- **What claude says after its last result is a turn too**, `Finished: false`, once a message of it has text: the turn under way, or one a stop cut short. Lines with no text (a resume's `system/init`, a message just sent, tool calls) make no turn, so the last reply is still there after a resume or a send.
- **Fewer** when the output has fewer, none when it has none. The file is read from its end, only as far back as the turns go.

### A session's spoken reply

A session writes for a screen: headings, tables, code, the question last. The voice (`GodMode.Voice`) follows it hands-free, so every session also gives a short spoken version of each reply, which the voice says word for word instead of summarising the written one (#384).

- **`speak(text)`** is on `/mcp`, beside `message_parent`. The server's MCP instructions (which Claude Code puts in the session's system prompt, so they hold on every turn) and the tool's description ask every session, whether the user is listening or not, to call it once per turn, from its main conversation, as the last thing before its final reply: one or two plain sentences, ending with its question if it has one. The full reply is written as usual.
- **It refuses a text the voice cannot say**, and says why, so the session fixes it and calls again: an empty one, one over 300 characters (its whitespace collapsed), and one written for a screen: markdown or code characters (`` ` `` `*` `_` `#` `|` `[` `]` `<` `>` `{` `}` `\` `~`, so "pull request 413", not "#413"), a URL, or a list. Its result, accepted, is `Kept as this turn's spoken reply: "…"`.
- **The turn's text is read from claude's stream**, not taken from the call, so it belongs to the turn it was made in: the `speak` tool use (`mcp__godmode__speak`) of an assistant line, whose tool result in the user line after is not an error (refused, or denied at its permission prompt). A subagent's call (a line with a `parent_tool_use_id`) is not the session's, and of several calls in a turn the last accepted counts.
- **`ProjectStatus.SpokenSummary`** is that text, set with `LastResult` as the turn's `result` comes, null for a turn that made none or ended in error, and cleared when the next turn starts (claude echoes a message the user sent). It is in `status.json`, so it lasts through a restart. `AttentionItem.Spoken` carries it for the turn's `Finished` item, or its `Question` in plain text; an AskUserQuestion, a permission and an error have none.
- **A turn without one** is as before: the voice summarises from the full question or result (#377).
- **It never asks the user**: every launch allows it, as it allows `message_parent`, with `--allowedTools` ([MCP Servers](#mcp-servers)), in any root and permission mode.

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
| `GODMODE_INPUT_*` | All form inputs (key in upper snake case, e.g. `GODMODE_INPUT_ISSUE_NUMBER`), but `__parentId`, which is `GODMODE_PARENT_ID` |
| `GODMODE_PARENT_ID` | Prepare and create scripts only: the full ID of the session starting this one, its parent ([A session's parent](#a-sessions-parent)), however the create named it. Unset for a top-level session |
| `GODMODE_RESULT_FILE` | Create scripts only: a file the script can write `key=value` lines to (see below) |
| `GODMODE_FORCE` | Delete scripts only: `true` when the user forced the delete |
| `GODMODE_ADOPT` | Create scripts only: `true` when the script runs to adopt a folder that exists (an action with `"adopt": true`), with the folder as `GODMODE_PROJECT_PATH`. It must make nothing: see [Adopting folders](#adopting-folders). Unset in a create |
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

- **The scripts run as a create's do:** `prepare`, then `create`, in the root (working directory and `GODMODE_ROOT_PATH`), with the root's and its profile's environment, the form's inputs (`GODMODE_INPUT_*`), `GODMODE_RESULT_FILE` and a parent's `GODMODE_PARENT_ID`, from the same environment allowlist. They get no `GODMODE_PROJECT_*`, `GODMODE_SESSION_ID` or `GODMODE_SHARED_FOLDER`: there is no project. Output streams as `CreationProgress`, and a non-zero exit fails the create.
- **Nothing is tracked, and no claude starts.** No working folder is made, no `.godmode/`, no project is listed or announced (`ProjectCreated`), and a failed script leaves no `Error` project behind: the create fails with its message, and its log stays.
- **The run has an id as a session would**, `yymmdd-<kind>-<slug>-<suffix>` (the action's name as the kind, the `name` input, if any, as the slug), for its log and result file (`{root}/logs/<id>.log`, `.result`) and its progress, whose ID `{profile}/{root}/<id>` names no project. The progress ends when `CreateProject` returns, which carries the message: no event marks the end.
- **Of the result file, only `message` is read.** `project_path`, `project_name`, `project_prompt` and `kind` are ignored, and their checks do not apply: nothing is made a project's folder, so a script may name a folder outside the root (the root it made) without being refused.
- **`CreateProject` returns no project** (`CreateProjectResult.Project` is null) and the script's `message`. The app shows the message, or that the action finished, and stays on the form; it offers no model for such an action (the listed action's `Session` is `false`, its `Model` null and `AllowSkipPermissions` false).
- **The roots are read again once it has run**, so a root it made reaches every client as `RootsChanged` at once, not at the next poll ([Live roots](#live-roots)).
- **What would give it a working folder is a config error**: `"session": false` with `"sharedFolder": true`, with `"scriptsCreateFolder": true`, or with no `create` script. A create of the action is refused saying why, and the listing leaves that action out, with a warning in the log, as it leaves out an overlay it cannot read: the root keeps its profile and its other actions. The merged action is checked, so a base `config.json` that sets one of these for its worktree actions needs `false` in the session-less action's overlay. `claudeArgs`, `model`, `effort`, `permissionMode`, `allowSkipPermissions`, `promptTemplate`, `delete`, `status` and the resume settings mean nothing to it and are ignored, since a base config shares them with every action.

### Adopting folders

A root often has folders GodMode did not make: worktrees made before GodMode or beside it, old shares. The app lists them under the root, folded as "Not in GodMode (N)", and **Adopt** makes one a session of the root, as it is. Which folders a root offers is the root's business, so the server stays VCS-agnostic: a `list` script says, and without one the root's subfolders are offered. Nothing is cached: each `ListUnmanaged` reads them again.

**Without a `list` script** the candidates are the root's immediate subfolders, in ordinal order, but the root's own (`.godmode-root`, `logs`, `.godmode`, `.archived`), hidden ones (a name that starts with `.`), a link that leads out of the root, and a name Windows would change. A root that is its own workspace ([below](#a-root-as-its-own-workspace)) offers none: its folders are its repo's. Each is offered under its folder's name, with no kind, action or inputs.

**A `list` script** (`config.json`'s `"list": "scripts/list.ps1"`, one script) runs in the root, with the root's and its profile's environment (`config.json`'s own `environment`, no action's) and `GODMODE_ROOT_PATH`, and prints one JSON array on stdout. Empty output is an empty array. Each item is a candidate:

| Field | | |
|-------|---|---|
| `path` | required | The folder: **directly in the root** (sessions are recovered only from there), absolute or relative to the root, and existing. Not one of the root's own folders, and not a link out of it |
| `name` | optional | What the app shows, and the session's name when no script names it. Default: the folder's name. At most 200 characters |
| `kind` | optional | What it is (`feat`, `bug`…): the app's label for it, and the session's kind when no script names one |
| `action` | optional | The root's action that adopts it (its name, as in `config.{action}.json`), which must start a session. Default: the root's first action |
| `inputs` | optional | An object of strings, numbers and booleans the adopt passes on, as a create form's inputs: the branch, an issue number. The app shows a `branch` input beside a candidate with no `kind` |

```powershell
# .godmode-root/scripts/list.ps1: each git worktree of the root, with its branch
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($env:GODMODE_ROOT_PATH).TrimEnd('\', '/')
$items = foreach ($line in git -C $root worktree list --porcelain | Select-String '^worktree ') {
    $path = [IO.Path]::GetFullPath($line.Line.Substring(9))
    if ([IO.Path]::GetDirectoryName($path) -ne $root) { continue }   # only folders directly in the root: not the root, nor one elsewhere
    $branch = git -C $path branch --show-current
    [ordered]@{ path = $path; name = (Split-Path $path -Leaf); action = 'issue'; inputs = [ordered]@{ branch = $branch } }
}
@($items) | ConvertTo-Json -Depth 5 -AsArray
```

Pipe the array into `ConvertTo-Json -AsArray` (`ConvertTo-Json -AsArray @(...)` with the array as its argument prints an array inside an array), with a `-Depth` of at least 3 for `inputs`.

- **Read strictly.** The output is untrusted: an item with a field not in the table, a `path` that is outside the root, nested, missing or listed twice, an `action` the root does not have, an input that is an object or an array, or anything but one JSON array, fails the whole listing, saying which item and why; so does a script that exits non-zero, prints more than 1 MB, lists more than 1000 folders, or runs longer than `ListScriptTimeoutSeconds` (30). The app shows the reason when the group is opened. A root config that cannot be read fails it too.
- **A hidden folder or a root of its own is never offered, nor adopted**, whatever the script says: a name that starts with `.` (a worktree list's `.bare`, the bare repository every worktree shares, whose delete would take them all) or a folder with a `.godmode-root` in it. A script's item for one is left out, not an error; an `AdoptFolder` of one is refused.
- **A folder a session is in is never offered**, whatever the script says: a tracked session works in it, a session has its state in its `.godmode/sessions/` (not recovered, say), or a create is making it. A folder whose only sessions are in its trash (deleted or forgotten) is offered.

**`AdoptFolder(profile, root, path, action, inputs)`** makes a session of the folder `path` (a candidate's `Path`, the folder's name), of `action` (null for the root's first), with `inputs` (the app passes the candidate's `name`, `kind` and `inputs`). The folder is used as it is: no folder is made, nothing in it changes but its `.godmode/`, and no `prepare` script runs.

- **Only an action that says `"adopt": true` runs a script**, its `create` script alone, in the folder, with the environment a create's has, the inputs as `GODMODE_INPUT_*`, **`GODMODE_ADOPT=true`**, and the folder as `GODMODE_PROJECT_PATH`. It must make nothing, no worktree and no branch: it names the session. Of its result file, `project_name`, `kind` and `project_prompt` are read (a `project_prompt` from an issue's title, say); a `project_path` is ignored, logged, since the folder is the one adopted. A script that fails fails the adopt, and nothing is left behind.
- **Any other action runs no script**: the server adopts the folder itself. The session's name is the input `name`, else the folder's; its kind the input `kind`, else the action's name; its prompt the input `prompt`, if any.
- **A missing or broken config never runs a create.** A root with no `config.json` adopts with the default action, which has no script. A config that cannot be read refuses the adopt, saying why, before anything is written or run.
- **Refused, changing nothing**, for a `path` that is not a folder directly in the root (`..`, a path outside it, a nested one, one of the root's own folders, a hidden folder, a root of its own, a folder that does not exist), and for a folder a session is in ("is in use").
- **The session** is an ordinary session of the root and its action (its environment, arguments, permission mode and delete), with `adopted: true` in its `settings.json` (`ProjectStatus.Adopted`, `ProjectSummary.Adopted`). With no prompt it starts idle, waiting for its first message, which starts a fresh Claude conversation: an earlier one in the folder is not resumed. It is pushed as `ProjectCreated`.
- **Deleting it** follows its root's rules, as any session's delete: for a worktree root, the delete script runs and the folder is removed. So the app offers **Forget (keep the folder)** beside Delete for an adopted session ([The trash, and folding](#the-trash-and-folding)).

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

### Issue Info

A root's `issueInfo` script (`config.json`'s `"issueInfo": "scripts/issue-info.ps1"`, one script) tells the server what an issue is, without the server knowing the VCS: `DescribeIssue` runs it now, in the root, with the root's and its profile's environment (`config.json`'s own `environment`, as the `list` script) and the issue as said in `GODMODE_INPUT_ISSUE`, and it prints one JSON object:

```json
{"title": "Voice create keeps its draft", "labels": ["bug", "voice"]}
```

Both are optional (`{}` is an issue with neither); `labels` is an array of strings (in PowerShell, a single label needs `[string[]]` or `-AsArray`, or `ConvertTo-Json` writes a string). Read strictly: unknown properties, other types, more than 64 KB of stdout, a non-zero exit or more than `ListScriptTimeoutSeconds` (default 30) fail the call, saying why. A root without one describes nothing (`null`).

Voice reads an issue's labels before it reads a create back (#473): an issue labelled with the name of another of the root's actions that takes an issue (`epic`) is proposed as that action, and the read-back says why. The action's own create script stays the backstop (GodMode's `issue/create.ps1` refuses an `epic`-labelled issue).

### Resuming After a Restart

When the server stops, it stops every project, and one that was `Running`, `WaitingInput` or `WaitingPermission` keeps that in `ProjectStatus.StateAtShutdown` (in `status.json`) beside `Stopped`. The marker is saved before the project is stopped, from what it was doing as the shutdown began, and nothing that happens during the stop changes it: not claude's answer to the interrupt (an `error_during_execution` result), not its exit, and not a server killed before the stop is done. A project whose stop by the user is still under way (in its grace period) when the shutdown begins is not marked. When it starts again, once it is listening and has recovered the projects, it carries on with them as the project's action says:

- **Working** (`Running`, or `WaitingPermission`, whose prompt the shutdown denied): resumed with `--resume <session-id>`, launched as any resume is, and sent `resumePrompt` as its first input. Three are resumed at a time, each until claude reports `system/init`.
- **Waiting on a question** (`WaitingInput`): no process is launched. The project is `WaitingInput` again with its `CurrentQuestion`, and still a `Question` in `GetAttention` (unless it was marked seen), until a reply (`ReplyAndResume`) resumes it with the answer. An AskUserQuestion's options do not survive: the shutdown denies it before it interrupts claude, and what is left is its first question's text, answered in the chat.
- **`resumeOnRestart: false`**: the project stays `Stopped`.

A project the user stopped, or that was `Idle`, `Stopped` or `Error` when the server stopped, is not resumed. A resume that fails is `Error` with `LastError`, as any resume is (a root config the launch cannot use, a claude that exits at once), and is not tried again. Any launch clears `StateAtShutdown`, and so does a stop by the user. A project waiting its turn is decided when it comes: one the user has stopped, resumed or answered meanwhile is left as it is. A shutdown while the start is still resuming launches nothing more, and the projects not resumed yet keep their marker for the next start. The marker is only a field in `status.json`: a `status.json` restored from a backup or copied from another machine carries it, and that project is resumed on the next start.

Sessions are off the server's console (see [Stopping a Session](#stopping-a-session)), so a Ctrl+C in the server's terminal reaches claude only through the server's shutdown. A claude that exits on its own just before a shutdown still counts as stopped by it: an exit no more than `ExitBeforeShutdownWindowSeconds` (default 5) before the shutdown, with nothing changed since, and the project keeps its question and is resumed like the rest. A server that is killed (no shutdown runs) leaves no `StateAtShutdown`, and its projects are recovered `Stopped`.

## Sessions

### One Project, One Claude

A project has at most one claude process at a time.

- **A create does not make a project that is there.** It is refused ("is in use") when a tracked project has its ID, or its folder unless both share it (`sharedFolder`, [below](#several-sessions-in-one-folder)) (on Windows compared as Windows compares paths, so `Fix` is the folder `fix`), before a folder is reused or any script runs, and nothing is written. So is a create script's `project_path` that is a tracked project's folder: the create is then `Error`, under its own ID, saying why, and the project in that folder keeps its claude and its files. "Reuse folder" (`__reuseExisting`) is for a folder no session uses: one with a session's state on disk in `.godmode/sessions/`, tracked or not, is in use too. A create that failed leaves its `Error` project, with its ID: delete it before creating it again.
- **A create that failed before its launch takes no input.** Its `Error` project (`CreateFailed` on its status and its attention item, `LastError` the create's failure) has no state and no claude, in memory only: `SendInput`, `ReplyAndResume`, `ResumeProject`, `StopProject` and the fleet's `send` refuse it ("failed to create … delete it, or create it again") and write nothing, into its folder or its inbox. The app offers its delete on its inbox item, and voice sends it no answer. Its delete runs no delete script when its folder was never made; a create script that made the folder before it failed has the delete script take it down, as any delete does.
- **One launch or stop at a time.** Create, resume, a reply that resumes (`ReplyAndResume`), stop, delete and the start carrying on after a restart take the project's lock, so a stop comes before a launch or after it, never in the middle of one, and two resumes launch one claude. A launch still starting, or a claude whose exit is not handled yet, is waited for, never taken for a stale `Running`.
- **A resume with nothing to say is `Idle`.** `ResumeProject` on a stopped project starts claude on its session, and claude writes nothing until it has input: the project is `Idle` ("resumed, waiting for you") until the user writes, rather than `Running` with nothing happening.
- **A launch that does not start says why.** A missing executable, a root config the launch cannot use, or a create script that failed leaves the project `Error` with `LastError`.

### A session's parent

A session may have a **parent**: the session that started it (an overseer starting a worker, say). It is `ProjectStatus.ParentId`, the parent's full ID, kept in `status.json` and listed in `ProjectSummary.ParentId`, so the app can nest sessions with no extra call; null for a top-level session.

- **Named at create, never changed.** Over the hub it is the `__parentId` input of `CreateProject`, a string (null or blank is none; any other kind of value is refused). It is an input, not a fifth parameter, because a hub method takes all its arguments: every caller that names no parent, the voice session and older apps among them, calls as it did. On the server it is `CreateProjectRequest.ParentId`. The prepare and create scripts get it as `GODMODE_PARENT_ID`, and its address as `GODMODE_PARENT_ADDRESS`, whichever way it came, and so does the child's claude; `__parentId` is no form input, so it is in no `GODMODE_INPUT_*` and no name or prompt template.
- **The fleet's `start_session` names it for a session that calls it:** the caller is its child's parent unless it says `top_level` or names another ([Overseer sessions](#overseer-sessions)).
- **A session of this server.** A parent this server does not track (another server's session, a deleted one, a typo) is refused before anything is created or run. One in any root or profile of this server is taken; but a child talks to a parent in another root only through a `Fleet:Links` entry, and the fleet's `start_session` from a session needs one to make it ([Messages between sessions](#messages-between-sessions)). An action that starts no session checks it too, and keeps it nowhere.
- **Metadata only.** The ID stays `{profile}/{root}/{id}`. Stopping, resuming or deleting a parent leaves its children as they are, and a restart recovers them with it. A deleted parent leaves its children naming a session that is gone, which the app shows as top level; nothing on the server reads it. - **A parent's new ID is its children's.** When a parent takes a new ID (its root's `profileName` edited, or its explicit entry renamed, at a restart or a read of the live roots), the server, once it has recovered it under that ID, rewrites the `ParentId` of every session naming the old one, in any root, saving each `status.json` as any change is, and pushes a child it does not announce anew as `StatusChanged`. So children stay under their parent, whether the root changed while the server ran or while it was down. Only a child the server does not track at that moment (its root missing, or held by another server) keeps the old ID.

### Background tasks

A session's claude can run work **in the background**: a subagent (`Agent` with `run_in_background`), a shell (`Bash` with `run_in_background`), a `Monitor`, or a `Workflow`. These keep running after its turn has ended, while the session is `Idle`, and each one that finishes wakes claude into a turn of its own. `ProjectStatus.BackgroundTasks` (and `ProjectSummary.BackgroundTasks`) lists them (issue #432), null when there are none: each `BackgroundTask(Id, Type, Description, Step)`.

- **claude's own list.** claude writes `system/background_tasks_changed`, its whole list of background tasks, whenever the list changes, between turns too, and `[]` when the last task ends. The server keeps that list. `system/task_started` is not the list: most of those are foreground shells. A task's `Type` is claude's `task_type`: measured with claude 2.1.295, `local_agent` is a subagent, `local_bash` a shell **or** a monitor, and `local_workflow` a workflow.
- **Each task's step.** `Step` is the `description` of the task's last `system/task_progress` ("Running the tests"), which subagents and workflows report and shells do not. A step changes the status as any change does: it is saved and pushed.
- **Gone with the process.** A claude that is killed (a stop, a server shutdown, a crash) writes no `[]`, and resumed, it only reports its old tasks `stopped`. So its exit, a stop and every launch clear the list, and a server start drops one a `status.json` kept. A `/clear` does not: the tasks outlive it, and their `[]` comes later. claude whose stdin closes waits for its background tasks before it exits.
- **Shown as work.** The app shows an idle session with background tasks as working in the background, with the task count, and lists the tasks and their steps in a tooltip; its header's button stops it. The fleet's `list_sessions` and `read` give the list, and voice says the session is working in the background.
- **Finished is not held for them.** A turn that ends while tasks run raises `Finished` as any turn does. A session that handed its work to background tasks says so with the `continuing` outcome ([A session's spoken reply](#a-sessions-spoken-reply)), which makes its turn quiet.

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
│           ├── output-generation # A GUID, new on each create and /clear: which output.jsonl a client's offset is in
│           ├── output-{generation}.jsonl # The output a /clear started over from, kept
│           └── session-id       # Claude's session GUID, for --resume
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
- **The sessions can read each other's state.** It is all inside their shared working directory: another session's `output.jsonl` and `status.json`. What speaks for a session is not: its MCP config, with its token, and the messages held for it are in `{root}/logs/` ([Messages between sessions](#messages-between-sessions)). Put sessions that must not see each other in separate folders.

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
- **Forget.** `ForgetProject(projectId)` takes any session out of GodMode and leaves its folder be: it stops it, runs **no delete script**, and moves only `.godmode/sessions/<id>/` to the trash, marked `forgotten` beside its `trashed-at`, never the folder or its files, whatever the root's rules for a delete are. It is pushed as `ProjectDeleted`, and says `Trashed`. The folder is then no session's, so it is offered for adopting again. `RestoreProject` undoes it as it undoes a delete, but brings the session back **as it was**: one that owned its folder owns it again (its next delete follows its root's rules, a worktree's folder included), so the restore fails when another session is in the folder now. The app offers Forget in an adopted session's delete, with "Forgot · Undo".
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

The server builds no React and needs no npm. To see the client, run the GodMode app (on Windows, `dotnet run --project src/GodMode.Maui/GodMode.Maui.csproj -p:Windows=true`) and add the server there with its key.

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
- `Task<CreateProjectResult> CreateProject(profileName, projectRootName, actionName, inputs)` — Create a project with form inputs (`actionName` null = default action): its `Project`, or, for an action that starts no session, no project and the script's `Message` ([Actions that start no session](#actions-that-start-no-session)); the `__parentId` input names its parent ([A session's parent](#a-sessions-parent))
- `Task SendInput(projectId, input)` — Send input to Claude (while a permission prompt or question waits, it answers that instead)
- `Task RespondToPermission(projectId, requestId, decision)` — Allow or deny the project's `PendingPermission`; fails when the request is not pending, another answer to it came first included
- `Task<PermissionDetail> GetPermissionDetail(projectId, requestId)` — Everything the pending permission request would run, to show before Allow (see [The MCP endpoint](#the-mcp-endpoint))
- `Task AnswerQuestion(projectId, requestId, answers)` — Answer the project's `PendingQuestion` (question text → chosen label or free text)
- `Task StopProject(projectId)` — Stop running project: interrupt claude, then kill its process tree after `StopGracePeriodSeconds` (see [Stopping a Session](#stopping-a-session))
- `Task ResumeProject(projectId)` — Resume stopped project; it is `Idle` until the user writes
- `Task SubscribeProject(projectId, fromOffset, subscriptionId, generation)` — Replay `output.jsonl` from `fromOffset` (the byte offset after the last line the client has; 0 for all, `-N` for the last N turns) in `OutputBatch` messages, then `OutputReplayComplete`, then live `OutputReceived` lines, each line once and in order. `subscriptionId` is the client's own, echoed by this subscription's batches and complete; `generation` is the output generation `fromOffset` is in (null when the client holds none), and a positive offset in any other replays from 0
- `Task UnsubscribeProject(projectId)` — Unsubscribe from output
- `Task<DeleteProjectResult> DeleteProject(projectId, force)` — Stop the project, run delete scripts and remove it; a refused delete leaves it `Stopped`. A session that shares its folder is moved to the folder's trash (`Trashed`), any other loses its working folder. A root config that cannot be read refuses the delete of a session that owns its folder (its delete script, which guards the folder, cannot run); `ForgetProject` still works. The app never sends `force` on its own: only after a refusal, as "Force delete…", confirmed
- `Task<ProjectStatus> RestoreProject(projectId)` — Undo a delete that trashed the session, or a forget: back under the same ID, `Stopped`, pushed as `ProjectCreated`; fails when its root is not listed under its ID's profile and name now, or it is not in the trash (see [The trash, and folding](#the-trash-and-folding))

Adopting folders ([Adopting folders](#adopting-folders)):
- `Task<UnmanagedFolder[]> ListUnmanaged(profileName, projectRootName)` — The root's folders no session is in, read now: its `list` script's (each `Path`, `Name`, `Kind`, `ActionName`, `Inputs`), else its immediate subfolders; fails, saying why, on a script that fails, times out or prints anything but the documented array
- `Task<ProjectStatus> AdoptFolder(profileName, projectRootName, path, actionName, inputs)` — Make a session of the folder as it is, running only an `"adopt": true` action's create script (`GODMODE_ADOPT=true`); idle without a prompt; pushed as `ProjectCreated`. Refused, changing nothing, for a path not directly in the root, or a folder a session is in
- `Task<DeleteProjectResult> ForgetProject(projectId)` — Take the session out of GodMode, its folder kept: no delete script, only its state to the trash, which `RestoreProject` undoes; pushed as `ProjectDeleted`

Attention:
- `Task<AttentionItem[]> GetAttention()` — Every project that needs the user (`Permission`, `Question`, `Error`, `Escalation`, `Review`, `Finished`), but a child's `Finished` and `Review` ([Quiet turns and escalation](#quiet-turns-and-escalation)), each with its `RecordedParentId`, oldest first, with a short plain `Text`, and for a turn's end (`Finished`, or a `Question` in plain text) the session's own `Spoken` reply when it gave one; the same after a restart
- `Task<AssistantReply[]> GetLastReplies(projectId, turns)` — What claude said in the project's last 1 to 20 turns, oldest first, whatever it waits on, seen or not; the last may be unfinished ([A session's last replies](#a-sessions-last-replies))
- `Task MarkSeen(projectId)` — What the project needs is seen: no longer `Finished` or `Escalation`, nor `Review` until the pull request changes, nor a `Question` in plain text until a turn asks again, nor `Error` until it fails again (a reply does the same). The project's state is unchanged: a seen question is still `WaitingInput`, and a reply still answers it. A pending AskUserQuestion or permission is unaffected: claude waits on it, and only an answer clears it
- `Task ReplyAndResume(projectId, text)` — `SendInput` to a running claude; otherwise resume, send, and return once claude reports `system/init` (fails on exit or after `SessionStartTimeoutSeconds`, default 60). A slash command GodMode does not send is refused, as by `SendInput` ([Slash commands](#slash-commands))

Roots and profiles:
- `Task<ProjectRootInfo[]> ListProjectRoots()` — Get roots with their actions and input schemas, and each root's `Title` when it has one
- `Task<IssueInfo?> DescribeIssue(profileName, projectRootName, issue)` — The issue's `Title` and `Labels` from the root's `issueInfo` script, run now; null when the root has none; fails, saying why, as [Issue Info](#issue-info) says
- `Task<ProfileInfo[]> ListProfiles()` — Get profiles (read-only: no hub method writes a profile or a root)

Utility:
- `Task<string?> CheckCommand(command)` — Resolve a command on the server's `PATH`

### Server → Client Events

- `OutputReceived(projectId, offset, rawJson)` — A live raw Claude JSON output line; `offset` is the byte offset in `output.jsonl` just after it
- `OutputBatch(projectId, subscriptionId, generation, fromOffset, lines)` — Replayed `OutputLine`s (`Offset`, `RawJson`) covering `output.jsonl` from `fromOffset`, for the subscription `subscriptionId`, in the file's `generation`; a replay from 0 when more was asked for, or in another generation, means the client's transcript is not from this file
- `OutputReplayComplete(projectId, subscriptionId, generation, offset)` — The subscription's replay is done at `offset`, in `generation`; live lines follow
- `OutputRestarted(projectId, generation)` — The output started over in `generation` (`/clear`), to the connections that follow it live: what they hold is gone, and the live lines that follow are the new file's, from its start
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
- `POST /mcp/fleet` — The fleet's MCP endpoint, for an overseer: the server's own credential, or a session with the fleet's tools with its project token. See [The fleet endpoint](#the-fleet-endpoint)

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
