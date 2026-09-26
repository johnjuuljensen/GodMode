# GodMode — Unified Architecture & Development Guide

This document is the single source of truth for Claude Code sessions working on GodMode. It describes the current system, its design principles, where to place new code, and how deployment works.

---

## 1. What GodMode Is

GodMode runs Claude Code sessions that ship issues, and lets you follow and steer them from a browser or a phone. It is a .NET 10 solution with two UI surfaces:

1. **React SPA** — served directly by GodMode.Server, accessed via browser
2. **MAUI app** — hosts the same React SPA in a HybridWebView, with a local proxy for multi-server connectivity

A server runs on a machine you own: a PC, a VM or a GitHub Codespace. Its **project roots** are directories on that machine, each with scripts and input schemas in `.godmode-root/`. **Profiles** group roots and carry shared environment variables. Both are maintained by hand on the host. You create **projects** from a root's actions. Each project is a folder with a Claude Code process working in it.

---

## 2. Solution Structure

```
GodMode.slnx
├── src/
│   ├── GodMode.Shared/            # Shared types, models, enums, hub interfaces
│   ├── GodMode.Server/            # ASP.NET SignalR server, spawns Claude processes, serves the SPA and the MCP endpoint
│   ├── GodMode.Client.React/      # React SPA (Vite + Zustand + SignalR), an npm project; its NoTargets csproj builds it
│   ├── GodMode.ProjectFiles/      # File system utilities for project folders
│   ├── GodMode.ClientBase/        # Shared .NET client abstractions (host providers, registry)
│   ├── GodMode.Maui/              # MAUI app (Android, iOS, macOS, Windows) — hosts React
│   └── SignalR.Proxy/             # SignalR WebSocket relay for MAUI
└── tests/
    └── GodMode.Server.Tests/      # xUnit tests for GodMode.Server
```

### Project Dependency Graph

```
GodMode.Shared  ← (no deps, shared by everything)
    ↑
GodMode.ProjectFiles
    ↑
GodMode.Server  ← GodMode.Server.Tests

GodMode.Shared    SignalR.Proxy  (WebSocket relay, LocalServer)
    ↑                 ↑
GodMode.ClientBase  ← (host providers, server registry, URL selection)  ← GodMode.Relay.Tests
    ↑
GodMode.Maui
```

`GodMode.Client.React/GodMode.Client.React.csproj`, a NoTargets project in the slnx, generates the hub types and runs `npm run build`. The server and the MAUI app reference it, so one build of either or both runs it once. The server copies its `dist/` into `wwwroot`; the MAUI app packages `dist/` as its `wwwroot`.

### Where to Put New Code

| What you're building | Where it goes |
|---|---|
| New shared model/enum/interface | `GodMode.Shared/Models/` or `GodMode.Shared/Enums/` |
| New hub method | `GodMode.Shared/Hubs/IProjectHub.cs` + `GodMode.Server/Hubs/ProjectHub.cs` |
| New server-side service | `GodMode.Server/Services/` — register in `Program.cs` |
| New React UI component | `src/GodMode.Client.React/src/components/{Feature}/` |
| New React store action | `src/GodMode.Client.React/src/store/index.ts` |
| New TypeScript hub type | Generated: add the C# type to `GodMode.Shared` and build GodMode.Server (`tools/GodMode.TypeGen` writes `signalr/generated/hub-types.ts`). Client-only types go in `signalr/types.ts` |
| Client-side .NET abstractions | `GodMode.ClientBase/` |
| File system project utilities | `GodMode.ProjectFiles/` |
| **All UI changes** | **React only** — never in .NET projects |

---

## 3. UI Architecture: React + MAUI

### 3.1 The Rule: All UI Lives in React

React is the **single UI implementation**. There is no native .NET UI. The MAUI app is a thin shell that hosts the React SPA in a WebView and provides a local proxy server for multi-server connectivity.

When building UI features:
- Build everything in `GodMode.Client.React/`
- Test in the browser against a running GodMode.Server
- The MAUI app will pick up changes automatically (React is rebuilt and embedded on MAUI build)

### 3.2 How MAUI Hosts React

```
┌─────────────────────────────────┐
│  GodMode.Maui                   │
│  ┌───────────────────────────┐  │
│  │  HybridWebView            │  │
│  │  (serves React from       │  │
│  │   embedded resources)     │  │
│  └───────────┬───────────────┘  │
│     bridge   │ WebSocket only    │
│ (relay.info, │                   │
│  servers.*)  │                   │
│  ┌───────────▼───────────────┐  │
│  │  LocalServer              │  │
│  │  (127.0.0.1:{port})       │  │
│  │  WS: /?serverId=X         │  │
│  │      &access_token=secret │  │
│  └───────────┬───────────────┘  │
│              │ SignalR.Proxy     │
│              │ (WebSocket relay) │
└──────────────┼──────────────────┘
               │
    ┌──────────▼──────────┐
    │  Remote GodMode     │
    │  Server(s)          │
    │  :31337              │
    └─────────────────────┘
```

**Build integration**: The MAUI csproj references `GodMode.Client.React.csproj`, which runs `npm run build`, and adds the React `dist/` as `MauiAsset` items under `wwwroot/`. HybridWebView serves these embedded files.

**Host bridge**: React talks to the shell over HybridWebView's raw-message channel (`services/hostBridge.ts` ↔ `Bridge/HostBridge.cs` + `Bridge/ShellBridge.cs`), a typed request/response API. `relay.info` returns the relay's base URL and a per-launch secret; `servers.list`, `servers.add`, `servers.remove`, `servers.start` and `servers.stop` manage servers; the `servers.changed` event says the list or a server's state changed. No bridge message carries a server's access token back to React.

**The relay** (`LocalServer`) serves only the WebSocket relay, and only to a request with the WebView's `Origin` (`https://0.0.0.1`, or `app://0.0.0.1` on Apple platforms; else 403) and the per-launch secret as the `access_token` query parameter (else 401). It forwards to registered servers by server ID and adds that server's key itself. SignalR frames pass through untouched, so the hub contract needs no relay changes.

**React detects hosting mode** via hostname:
```typescript
// hostApi.ts
export const isMaui = window.location.hostname === '0.0.0.1';
```

### 3.3 Browser vs MAUI: What Differs

| Concern | Browser (direct) | MAUI (via proxy) |
|---|---|---|
| React source | Served by GodMode.Server `/wwwroot` | Embedded in MAUI resources |
| SignalR connection | Direct to server `/hubs/projects` | Via LocalServer WebSocket relay |
| Server discovery | Single server (the one serving the page) | Multiple servers via the bridge (`servers.list`) |
| Authentication | API key entered once, kept in that browser (Section 4.4) | Access token per server in platform secure storage (Android Keystore, DPAPI, Keychain), added by the relay; React sends the relay only its per-launch secret |
| SignalR negotiate | Standard | Skipped (`skipNegotiation: true`, relay handles it) |
| Server management | Not available | Add/remove/start/stop servers via the bridge (`servers.*`) |

### 3.4 What MAUI Developers Need to Know

The MAUI project (`GodMode.Maui/`) contains:
- `MainPage.xaml` + `MainPage.xaml.cs` — HybridWebView setup, attaches the bridge, Windows DevTools integration
- `MauiProgram.cs` — DI registration (using `ServiceCollectionExtensions.cs` from GodMode.ClientBase), starts the relay
- `MauiSecretStore.cs` — `ISecretStore` over MAUI `SecureStorage`
- `Bridge/` — `HostBridge` (the message channel), `ShellBridge` (the shell API), `ShellMessages.cs` (its types)
- `Platforms/` — Platform-specific entry points (minimal). Android disables backup and device transfer.

**Key services in GodMode.ClientBase/**:
- `IServerRegistryService` — server registrations in `~/.godmode/servers.json` (each with a GUID ID and an ordered URL list); tokens in `ISecretStore`, never in the file
- `IServerDirectory` — builds an `IServerProvider` per registration (local servers, GitHub Codespaces), lists servers and resolves one to a relay target
- `ServerUrlSelector` — picks a server's URL: the first, in order, that answers `/health` within 1.5 s. Checked on every relay connection; a network change drops the relays so they reconnect and check again

**SignalR.Proxy/** handles the WebSocket relay:
- `LocalServer` — the loopback listener with the Origin and secret checks (tested in `tests/GodMode.Relay.Tests`)
- `SignalRRelay` — bidirectional message relay with proper SignalR framing

### 3.5 Considerations When Changing React

When making React changes, keep these MAUI constraints in mind:

1. **No server-relative URLs** — React may be served from `0.0.0.1` (MAUI) or from the server. Make HTTP calls through the helpers in `hostApi.ts`, which resolve the base URL for both modes.

2. **SignalR connection differences** — Use `getHubUrl(serverId)` and `getHubOptions(serverId)` from `hostApi.ts`. Never hardcode hub paths.

3. **Multi-server support** — In MAUI, React manages connections to multiple servers. The store's `ServerConnection[]` array and `serverId` parameters exist for this reason. Don't assume a single server.

4. **No browser-only APIs without fallback** — HybridWebView is not a full browser. Avoid APIs that may not be available (e.g., `window.open`, `navigator.clipboard` may need fallbacks).

5. **Offline-capable assets** — All React assets are embedded. Don't rely on CDN-hosted fonts, icons, or scripts. Bundle everything.

6. **Test both modes** — After significant changes, verify in both browser (direct to server) and note any MAUI-specific behavior (server discovery, multi-server, auth flow).

---

## 4. Key Architectural Patterns

### 4.1 SignalR Communication (Strongly Typed)

All real-time communication uses strongly-typed SignalR on one hub, `/hubs/projects`:

- **`IProjectHub`** (Shared) — Client→Server methods
- **`IProjectHubClient`** (Shared) — Server→Client callbacks
- **`ProjectHub`** (Server) — Implements `Hub<IProjectHubClient>, IProjectHub`
- **`HubConnectionFactory`** (ClientBase) — .NET client side: `IServerProvider.ConnectAsync` returns a raw `HubConnection`, and consumers call `CreateHubProxy<IProjectHub>()` (`TypedSignalR.Client`) for typed calls
- **`signalr/hub.ts`** (React) — the TypeScript mirror, kept in step with the interfaces by hand

The hub is the session loop plus reading profiles and roots:

| `IProjectHub` (18 methods) | |
|---|---|
| Projects | `ListProjects`, `GetStatus`, `CreateProject`, `SendInput`, `StopProject`, `ResumeProject`, `SubscribeProject`, `UnsubscribeProject`, `DeleteProject` |
| Prompts | `RespondToPermission`, `GetPermissionDetail`, `AnswerQuestion` |
| Attention | `GetAttention`, `MarkSeen`, `ReplyAndResume` |
| Roots | `ListProjectRoots` |
| Profiles | `ListProfiles` |
| Utility | `CheckCommand` |

| `IProjectHubClient` (8 callbacks) |
|---|
| `OutputReceived`, `OutputBatch`, `OutputReplayComplete`, `StatusChanged`, `AttentionChanged`, `ProjectCreated`, `CreationProgress`, `ProjectDeleted` |

**A project's ID is `{profile}/{root}/{project-folder}`**, where its folder is, so one folder name in two roots is two projects. Every hub method and callback that names a project takes or gives this ID (`ProjectStatus.Id`, `ProjectSummary.Id`). Clients treat it as opaque and pass it back as they received it; the server derives it from the folder's location on every recovery. Root scripts get the folder name alone, as `GODMODE_PROJECT_FOLDER`.

When adding a new hub method:
1. Add to `IProjectHub` (client→server) or `IProjectHubClient` (server→client)
2. Implement in `ProjectHub`
3. Build GodMode.Server: it regenerates `signalr/generated/hub-types.ts`; commit the change
4. Wire up in `signalr/hub.ts` (GodModeHub class)
5. Expose in Zustand store if UI needs it

### 4.2 Config-Driven Project Roots

A root is a subdirectory of `ProjectRootsDir` that contains `.godmode-root/`. The server discovers roots by scanning that directory on each call, so a root added on the host shows up on the next refresh.

```
root-name/
├── .godmode-root/
│   ├── config.json                # Base config (profileName, prepare, delete, status, environment, claudeArgs, permissionMode, allowSkipPermissions, resumeOnRestart, resumePrompt)
│   ├── config.{action}.json       # Per-action overlays (merged with base)
│   ├── {action}/
│   │   ├── schema.json            # Input form schema (JSON Schema)
│   │   └── create.ps1             # Action-specific creation script
│   └── scripts/
│       ├── prepare.ps1            # Shared prepare script
│       ├── delete.ps1             # Shared delete script
│       └── status.ps1             # Reports the project's pull request (optional)
└── {project-folder}/              # Projects created from this root
```

**Merge order**: `config.json` (base) → `config.{action}.json` (overlay). Action overlay wins on conflict.

**Profile assignment**: `profileName` in `config.json` puts the root in that profile. Roots without it go to `Default`.

**MCP servers** are not root config: a repo brings its own, and GodMode adds only its own MCP endpoint (Section 8).

**Permissions** are the root's, not the project's. `permissionMode` (`acceptEdits`, `auto`, `manual`, `dontAsk`, `plan`; `bypassPermissions` is refused, and any other value is a config error) is passed as `--permission-mode`, beside the permission prompt (8.2). A create keeps it in the project's `settings.json`, and its launches reuse it after the root's config changes, as they reuse its model. `allowSkipPermissions` (default `false`) is the only way a session runs with `--dangerously-skip-permissions`: where it is false the create form does not offer Skip Permissions and the server refuses a create that asks. Every launch (create, resume, a reply's resume, a restart's) passes the flag only when the project's `settings.json` asks for it and the root's config, read then, allows it for every action (that file names the project's action too). That file is in the project folder, which the session can write, so it only asks: a project whose `settings.json` asks under a root that does not allow it launches without the flag, and the server logs a warning once. With skip, the permission mode is left out. The server README (*Permissions*) has what each mode was measured to send to the prompt.

**Pull request status**: a root's optional `status` script prints the project's pull request as JSON (`{"pullRequest": {url, number, state, review}}`, or `{}`), and the server keeps it in `ProjectStatus.PullRequest` in `status.json`. It runs on each transition to Idle or Stopped and, while the pull request is open, every 10 minutes. Only that schedule is in memory. The server parses the output strictly and knows nothing of the VCS.

**Resuming after a restart**: the shutdown records what an active project was doing in `ProjectStatus.StateAtShutdown` (`status.json`), before it stops the project, and nothing that happens during the stop (claude's answer to the interrupt, its exit) changes it; a project whose stop by the user is under way is not marked. After recovery, once the server listens, a project that was working (`Running`, or `WaitingPermission`, whose prompt the shutdown denied) is resumed and sent the action's `resumePrompt`, three at a time; one waiting on a question is `WaitingInput` again without a process, until a reply resumes it, so nothing answers it for the user. `resumeOnRestart: false` keeps it `Stopped`. A user stop, and any launch, clear the marker. The server README has the details.

Key services:
- `RootConfigReader` — discovers and merges configs fresh on each operation (no caching, no restart needed)
- `ScriptRunner` — executes scripts with cross-platform extension resolution (`.ps1` runs under `pwsh` on every OS)
- `TemplateResolver` — resolves `{fieldName}` placeholders in name/prompt templates

`src/GodMode.Server/README.md` documents every config field, the script environment and the input schema.

### 4.3 Project Folder Structure

```
{root}/{project-folder}/
├── .godmode/
│   ├── status.json      # Current state, metrics
│   ├── settings.json    # Per-project settings (action, permission mode, skip-permissions asked for)
│   ├── input.jsonl      # User input log
│   ├── output.jsonl     # Claude output stream
│   ├── session-id       # Claude session ID for resumption
│   └── .gitignore
└── (project files)      # Working directory for Claude
```

A project is a folder directly inside its root with a `.godmode/status.json`, unless it is one of the root's own folders (below), which recovery skips. `{project-folder}` is its folder name, not its ID (4.1). The server does not archive or move project folders.

**`.godmode/.gitignore` is ensured on every launch** (`ProjectFolder.EnsureGitIgnore`), before the MCP config with the project token is written: created when missing, since a create script's checkout can bring a `.godmode/` without one, and given the `*` rule when it lacks it.

**A root's own folders are no project's.** `.godmode-root`, `logs` and `.archived` (a leftover) are refused as a project's folder (`ProjectFolder.ReservedFolderNames`), from the create dialog, a reuse, or a create script's `project_path`, and one found on disk with a `status.json` is not recovered. So are folder names Windows would change or not make a folder of, on every OS: a trailing dot or space (a name's trailing dots are dropped instead), and device names like `CON`, `NUL`, `COM1`. **A project's folder is strictly inside its root.** A create script's `project_path` must be inside that script's root, links followed, and not inside one of the root's own folders; the server deletes a project's folder only if it is inside a configured root by the same test, and otherwise leaves it on disk. **A project folder's files are untrusted:** its session can write them, so what reaches the `claude` command line or a link is checked when read back: `session-id` must be a GUID (anything else is no session, and the resume starts a fresh one), and a recovered pull request URL must be http(s).

**One project, one claude.** A project has at most one claude process. A create is refused while a tracked project has its ID or folder, before anything is written; create, resume, stop and delete of one project take its lock, so launches and stops come one at a time. Each session runs in a process tree of its own, off the server's console (a Job Object and a hidden console on Windows, a process group started through `setsid` on Linux). A stop interrupts claude (Ctrl+Break in its console on Windows, SIGINT to its group elsewhere), gives it `StopGracePeriodSeconds` (10) to exit, then kills the whole tree; the server's shutdown does the same for every session at once. The server README (*Sessions*) has the details and what claude was measured to honour.

**Failures keep the true state.** A project's state follows claude, whatever else fails:
- A `status.json` that cannot be saved does not fail the change: it is pushed, its events are raised, and it is saved with the next change (the failure is logged at Error).
- A line whose append to `output.jsonl` fails is tried once more on the file opened again, cut back to the last whole line, so offsets stay the file's; a line that cannot be persisted is not broadcast. The project's one consumer survives a failing item, and is started again if it faults; a subscribe or stop waiting on it fails rather than hangs.
- Pushes to clients (output, status, attention) are started in order and not waited for (`ClientSends`), so a client that stops reading holds up only its own messages, not a project or the attention list.
- The echoed user line (`--replay-user-messages`, `isReplay`) sets Running, so a result of an earlier turn handled after a send does not stand through the next turn. claude 2.1.282 folds a message sent in the middle of a turn into that turn, echoing it at its next step.
- A connection's hub calls run four at a time (`MaximumParallelInvocationsPerClient`), so a reply that waits for a resumed claude's session leaves the tab its Stop and subscribes; one connection's subscribes still run one at a time, in order.

### 4.4 Authentication

Every request needs a credential, whatever the server is bound to, loopback included. The server picks exactly one mode at startup (`AuthModeSelector` in `Auth/AuthMode.cs`):

1. **Codespace** — `CODESPACES=true`. Callers present a GitHub token owned by `GITHUB_USER`, other than the codespace's own `GITHUB_TOKEN`, which its sessions are given.
2. **API key** — anywhere else. Callers send `Authorization: Bearer <key>` (the SignalR client sends it as `access_token` on the WebSocket upgrade). The key is `Authentication:ApiKey`, else the one in the server's key file (`Auth/ApiKeyFile.cs`): generated on the first start (256 bits), printed once, owner-only, and reused on every start. The file is in the server's own data directory (`%LOCALAPPDATA%\GodMode.Server\api-key` on Windows, `~/.local/share/GodMode.Server/api-key` on Linux and in the Docker image), or `Authentication:ApiKeyFile`, and never under `ProjectRootsDir`.

**Browser origins** (`Auth/OriginPolicy.cs`). A request with an `Origin`, as a browser sends on every WebSocket upgrade and any request but a same-origin GET, is refused with 403 before authentication unless it is one of the server's own origins: each address it listens on (a loopback or wildcard address also stands for `localhost`, `127.0.0.1` and `[::1]` on its port), those in `Authentication:AllowedOrigins` (a reverse proxy's, a host name's), a codespace's forwarded port, and the Vite dev server in Development. A request with no `Origin` (the MAUI relay, the attention service) needs the key alone.

Only `/health` and the SPA's static files are anonymous. The MCP endpoint, `/mcp`, takes a per-project token instead, and nothing else (Section 8.2). A Claude process and a root script start from an environment allowlist (`ChildEnvironment`), so the key reaches neither. Sessions still run as the server's OS user, so one that can run arbitrary commands can read the key file: the permission prompt is a gate, not a sandbox. `src/GodMode.Server/README.md` has the full binding guide.

### 4.5 React Client Architecture

- **State**: Zustand store (`store/index.ts`) — single flat store with computed properties
- **Transport**: SignalR hub class (`signalr/hub.ts`) — manages connection, exposes typed methods
- **Components**: Organized by feature in `components/{Feature}/`
- **Styling**: CSS files per component + shared `settings-common.css`
- **No router** — navigation via `activePage` state and `selectedProject`

Active page is a union: `appSettings | addServer | editServer | createProject`. Setting `activePage` shows the page; selecting a project clears it.

---

## 5. Design Principles — Files Are the Source of Truth

These principles govern how configuration and state work in GodMode. All new features must respect them.

### 5.1 Files on Disk Are the Only Source of Truth

Roots, profiles and projects are files and directories on the server's machine. You change configuration by editing those files, with an editor, a script or git, on the host. The server reads them fresh on each operation, so a change takes effect without a restart.

Never introduce a parallel config system: no override stores, no runtime-only state files, no in-memory caches that outlive a request.

**Wrong**: A `~/.godmode/profile-overrides.json` layered on top of `appsettings.json`.
**Right**: Editing the profile's config file directly.

### 5.2 The Config File Layout Is the Contract

The files on disk are the whole interface to configuration. There is no translation layer and no second format: if you can read the files, you understand the system state. Adding config means adding a file; removing config means removing it.

### 5.3 The Server Consumes Config; It Does Not Author It

The server reads roots, actions, schemas and profiles. It does not edit, package, import or export them, and no hub method writes config. There is no in-app editor, file browser, connector catalog or manifest. Nor does it provision MCP servers or archive projects: a client that wants to hide projects does so on its own side.

### 5.4 Roots Are External

Root definitions live wherever you keep them, typically a git repo you clone into `ProjectRootsDir`. The server doesn't bundle root templates in its binary. `.devcontainer/godmode-server/roots/` is one such set, which the codespace copies into place.

---

## 6. File-Based Profile Configuration

Profiles live under `.profiles/` in `ProjectRootsDir`. Adding a profile means adding a directory, not editing a shared config file.

### Layout

```
{ProjectRootsDir}/
├── .profiles/
│   ├── default/
│   │   ├── profile.json           # { "description": "..." }
│   │   └── env.json               # { "KEY": "value", ... }
│   └── production/
│       ├── profile.json
│       └── env.json               # "CLAUDE_CONFIG_DIR": "..." pins its sessions to one Claude account
├── feature-root/
│   └── .godmode-root/
│       └── config.json            # "profileName": "production" puts this root in that profile
└── bugfix-root/
    └── .godmode-root/
        └── ...
```

### Key Properties

- **Adding a profile** = `mkdir .profiles/{name}` + write `profile.json`, on the host. No hub method creates, edits or deletes one
- **Deleting a profile** = `rm -rf .profiles/{name}`
- **MCP servers** are not profile config: user-scoped ones live in the profile's `CLAUDE_CONFIG_DIR` (Section 8.1)
- **Git works** — the entire `{ProjectRootsDir}` can be a git repo
- **Profile env from the server's environment** — with `stripEnvVarProfile` in a root's config (or `{PROFILE}_STRIP_ENV_VAR_PROFILE=true` in the server's environment), server variables prefixed with the profile name (`MEGA_GITHUB_TOKEN`) reach that profile's sessions without the prefix (`GITHUB_TOKEN`)

---

## 7. Server Services Reference

| Service | Responsibility |
|---|---|
| `ProjectManager` | Central orchestrator — project lifecycle, profile/root snapshot, environment and launch config building |
| `ClaudeProcessManager` | Spawns Claude Code processes via `System.Diagnostics.Process`, each in a process tree of its own (`SessionProcessTree`: a Job Object on Windows, a process group on Linux), writes their output to `output.jsonl`, and stops them: interrupt, grace period, then the tree |
| `RootConfigReader` | Discovers and merges `.godmode-root/` configs |
| `ScriptRunner` | Executes cross-platform scripts (`.ps1` via `pwsh`, or `PowerShell:Executable`; `.sh` via `bash`, `.cmd`/`.bat` on Windows) |
| `ProfileFileManager` | Reads the `.profiles/` directory structure |
| `StatusUpdater` | Updates `status.json` during execution |
| `TemplateResolver` | Resolves `{field}` placeholders |
| `EnvironmentExpander` | Expands `${VAR}` in config values and strips profile prefixes from server env vars |
| `QuestionDetection` | Detects when Claude's turn ends in a question for the user |
| `PullRequestPoller` / `PullRequestScript` | When a root's status script runs for each project, and the strict reading of its output (owned by `ProjectManager`, not registered) |
| `PermissionPromptTool` | The MCP endpoint's one tool, claude's permission prompt (Section 8.2); registered with `AddMcpServer`, made per call |

All services are registered as **singletons** in `Program.cs`, except the MCP tool. Authentication lives in `Auth/` (`AuthModeSelector`, `ApiKeyFile`, `OriginPolicy`, `GodModeAuthenticationHandler`).

---

## 8. MCP Servers

### 8.1 Where a Session's MCP Servers Come From

GodMode gives a session one MCP server, its own MCP endpoint (8.2). It configures no others:

| Source | Where |
|---|---|
| The repo | its `.mcp.json` (Claude Code's project scope) |
| The profile's Claude account | user scope in the `CLAUDE_CONFIG_DIR` the profile's or root's `environment` sets |

The server writes the session's MCP config, its own entry alone, to `.godmode/mcp-config.json` in the project (owner-only where the OS allows) and passes it with `--mcp-config`; the file is deleted when the process exits. A root or action config that still has `mcpServers`, or a profile with an `mcp/` folder, launches normally: it is logged once as a warning, and ignored.

**Nothing is pre-approved.** GodMode passes no `--allowedTools`. A tool call that needs approval, an MCP tool's included, reaches the permission prompt (8.2), unless Claude Code's own settings allow it (`permissions.allow` in the profile's `CLAUDE_CONFIG_DIR`, or the repo's `.claude/settings.json`), the root's permission mode lets it through, or the project runs with skip-permissions, which only a root with `allowSkipPermissions` allows (4.2).

### 8.2 The GodMode MCP Endpoint

The server hosts one MCP endpoint, `/mcp` (`ModelContextProtocol.AspNetCore`, streamable HTTP, stateless), in-process. It has one tool, `permission_prompt`, which exists for claude's `--permission-prompt-tool` alone. It offers no other tools, for sessions or for operators.

Each session's MCP config has one entry for it, `godmode`: `"type": "http"`, the endpoint's URL, and headers carrying `Authorization: Bearer <project token>` and `X-GodMode-Project-Id`. The URL is an address this machine reaches the server on, picked from the addresses it is bound to (a loopback binding first, a wildcard's loopback next, else the one IP bound). The token is issued afresh for each launch and lives only in memory and in that file; claude's environment carries no `GODMODE_*` variable. `/mcp` takes only a project token, for the project it was issued to: not the user's API key. A project token opens nothing else: not the hub, not `/api/*`.

**Permission prompts.** claude is launched with `--permission-prompts host --permission-prompt-tool mcp__godmode__permission_prompt`, so a tool call that needs approval waits for the user instead of being denied. claude calls `permission_prompt` with `{tool_name, input, tool_use_id}`. The call waits until the user answers, however long that takes, and returns claude `{"behavior":"allow","updatedInput":{…}}` or `{"behavior":"deny","message":"…"}` as its text. While it waits, it sends a progress notification every `PermissionPromptKeepAliveSeconds` (30): claude gives up on a tool call that sends no response or progress for 300 seconds. When claude cancels the call, or its connection drops, the request is withdrawn. The project is `WaitingPermission` with `ProjectStatus.PendingPermission` (a server-built one-line `Summary` such as `Bash: git push origin x`), answered with the hub's `RespondToPermission`. What is pushed stays slim: `PendingPermission` carries no tool input, to clients or to `status.json`, since a `Write` can be megabytes and the request rides every status and attention push. The server keeps the input in memory for the call's life, which is the request's; a client fetches `GetPermissionDetail` (the whole command, a write's path and new text, or an edit's path and each replacement with its old text, new text and `replace_all`, cut at 16 KB with `DetailTruncated`) when it shows the request, and its Allow waits for that detail to be on screen. Only one answer counts: a second, from another client or after the request was withdrawn, fails. The same flag makes claude offer `AskUserQuestion` in `--print` mode, and ask it through the same tool: that is `WaitingInput` with `PendingQuestion`, answered with `AnswerQuestion`. A request survives a client disconnect, not a stop or a server restart: the stop denies it before it interrupts claude, and an AskUserQuestion's first question stays the project's `CurrentQuestion`, so a restart finds it waiting on the user. A `result` withdraws any request still listed from the turn it ends. A chat message sent while one waits answers it (a single question takes it as its answer; otherwise it is a deny carrying the text).

**Attention.** `GetAttention` answers "what needs me on this server": one `AttentionItem` per project, oldest first, of kind `Permission`, `Question` (an AskUserQuestion, carried whole, or a turn that ended on `?`), `Error`, `Review` (changes requested on the project's open pull request) or `Finished` (a result the user has not seen; it and `Review` carry `PullRequestUrl`). It is derived from the status alone, and every field it reads is in `status.json` (`LastResult`, `LastResultAt`, `QuestionAt`, `SeenAt`, `PullRequest`), so a restart does not change the answer; `MarkSeen` and any reply move `SeenAt`. `AttentionChanged` pushes the whole list after a status push, only when the list differs from the last one pushed. `ReplyAndResume` answers any of it: `SendInput` to a running claude, otherwise a resume, the text, and a wait for `system/init`. claude writes nothing, not even `system/init`, until it has read its first input, so the text is sent first; the wait ends with an error if claude exits first or the `SessionStartTimeoutSeconds` setting (60) passes, and the text is sent again if the resume found no conversation and a fresh session replaced it.

---

## 9. Deployment Architecture

### 9.1 Where GodMode Runs

GodMode.Server runs on a machine you own, where Claude Code sessions can use the Claude subscriptions logged in there. Give a root its own `CLAUDE_CONFIG_DIR` in its `environment` to pin it to one subscription. There is no per-user cloud provisioning.

| Target | How | Auth Mode | Use Case |
|---|---|---|---|
| **A PC or VM** | `dotnet run`, or a published build | API key (generated on the first start, or configured) | The main setup: sessions run on your hardware |
| **GitHub Codespaces** | `.devcontainer/godmode-server/` | Codespace token | A disposable server per developer |
| **Docker** | Image from `src/GodMode.Server/Dockerfile` | API key (set it, so a replaced container keeps it) | A containerized server on your own host |

### 9.2 On a PC or VM

```bash
# Same machine only. With no Authentication__ApiKey, the first start generates a key and prints it
dotnet run --project src/GodMode.Server/GodMode.Server.csproj

# Reachable from your phone over Tailscale: keep the loopback binding (the same key works on both)
dotnet run --project src/GodMode.Server/GodMode.Server.csproj -- \
  --urls "http://127.0.0.1:31337;http://$(tailscale ip -4):31337"
```

For a long-running server, `dotnet publish -c Release` and run the output. Put roots in `ProjectRootsDir` (default `roots` under the working directory). The machine needs `claude`, `git`, `pwsh` and whatever the roots' scripts call, such as `gh`. GodMode itself needs no Node; a repo whose `.mcp.json` starts `npx` MCP servers does.

### 9.3 GitHub Codespaces

```
.devcontainer/godmode-server/
├── devcontainer.json     # Image, features, lifecycle scripts
└── roots/                # Pre-configured project roots
```

**Lifecycle**:
- `postCreateCommand`: clones `master`, publishes the server to `/opt/godmode-server`, copies `roots/` to `~/roots`, installs Claude Code
- `postStartCommand`: starts the server with `--ProjectRootsDir roots` from `$HOME`, sets port 31337 to public

**Server URL**: `https://<codespace-name>-31337.app.github.dev/`

**Secrets**: `gh secret set ANTHROPIC_API_KEY --repos owner/repo --app codespaces`

### 9.4 Docker Image

`src/GodMode.Server/Dockerfile` builds a multi-stage image:

```
Stage 1: Node 22 — builds React client (npm run build)
Stage 2: .NET SDK 10.0 — restores and publishes GodMode.Server
Stage 3: .NET ASP.NET 10.0 runtime — final image (`runtime` target)
Stage 4: runtime + .NET SDK — the `:sdk` tag, for sessions that build .NET code
```

The runtime image includes the published server and SPA, git, curl, Node 22 (for repos' `npx` MCP servers and JavaScript toolchains), PowerShell 7, the GitHub CLI and Claude Code, running as the non-root `godmode` user on port 31337. It sets `URLS=http://+:31337`. Run it with `-e Authentication__ApiKey=<key>`: without one it generates a key into the `godmode` user's home, which a replaced container does not keep. Mount a volume at the `ProjectRootsDir` path (`/app/roots` by default) to keep roots and projects across container replacements. The server manages local processes, so run one instance per workspace.

**Nothing a session runs as can change the server.** `/app` (the server and `wwwroot`) is root's and read-only to `godmode`, which owns only what the server writes: `/app/roots`, `/app/projects` (the default root when none is configured), `/data` and its home. Claude Code is installed root-owned with `npm install -g` (`/usr/bin/claude`), with self-update off (`DISABLE_AUTOUPDATER`, also in `/etc/claude-code/managed-settings.json`, since a claude process's environment is an allowlist). The server starts `claude` and `pwsh` by full path (`Claude__Executable=/usr/bin/claude`, `PowerShell__Executable=/usr/bin/pwsh`), and `/home/godmode/.local/bin`, which a session can write, is last on the `PATH`. Off Docker the two settings default to a `PATH` lookup (`claude`, `pwsh`): a codespace's claude is in `~/.local/bin`, installed by `postCreateCommand`, and the server runs as the sessions' user there anyway.

GitHub Actions (`.github/workflows/build-and-push.yml`) builds and pushes both targets to GHCR (`ghcr.io/johnjuuljensen/godmode`) on pushes to `master` that touch `src/`, `tests/` or the slnx (`latest`, `sdk`), and on a published release (plus the release tag).

### 9.5 Persistent Workspace

Every target separates the **server binary** from the **workspace data**:

```
/opt/godmode-server/          # Server binary (replaced on updates)
├── GodMode.Server.dll
├── appsettings.json          # Static config (URLs, auth, logging)
└── wwwroot/                  # React SPA

~/roots/                      # Workspace data = ProjectRootsDir (persists across updates)
├── .profiles/                # Profile definitions
│   └── default/
│       ├── profile.json
│       └── env.json
└── my-root/                  # Project roots
    ├── .godmode-root/
    └── {project-folder}/     # Projects

~/.godmode-logs/              # Server logs (relative to the working directory)
```

**Key principle**: Server updates replace the binary without touching workspace data. The server reads `ProjectRootsDir` from config to find it. On startup it recovers the projects it finds there.

---

## 10. Configuration Reference

### appsettings.json (Server)

Contains only infrastructure config — not domain data:

```json
{
  "Logging": { "LogLevel": { "Default": "Information" } },
  "AllowedHosts": "*",
  "Authentication": { "ApiKey": "" },
  "ProjectRootsDir": "roots",
  "Urls": "http://127.0.0.1:31337"
}
```

An empty `Authentication:ApiKey` means the key file's (Section 4.4). Every key can also be set as an environment variable (`Authentication__ApiKey`) or a command-line argument (`--ProjectRootsDir=...`). Domain data (profiles, roots) lives in the file tree under `ProjectRootsDir`, not in appsettings.json.

---

## 11. Adding New Features — Checklist

When building a new feature on GodMode:

1. **Check the design principles** (Section 5). Does your feature keep its state in files under `ProjectRootsDir`, read fresh from disk? Does it avoid shadow state and in-app config authoring?

2. **Choose the right layer**:
   - Server-side logic → `GodMode.Server/Services/`
   - Shared types → `GodMode.Shared/Models/`
   - UI → React (`GodMode.Client.React/`)
   - Client .NET abstractions → `GodMode.ClientBase/`

3. **All UI work goes in React** — no native .NET UI. The MAUI app is a thin host.

4. **For new config data**: Represent it as a file or directory under `ProjectRootsDir`, not as a section in `appsettings.json`. Adding config = adding a file. Removing config = removing a file.

5. **For new hub methods**: Add to the interface in Shared, implement in Server, add TypeScript types, wire into the React store.

6. **For new UI pages**: Create a component directory under `components/`, add an `activePage` variant in the store, add CSS alongside the component.

7. **Test both hosting modes**: Verify the feature works in browser (direct to server) and consider MAUI constraints (multi-server, proxy, embedded assets).
