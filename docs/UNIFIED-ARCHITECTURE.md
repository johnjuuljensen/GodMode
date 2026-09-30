# GodMode — Unified Architecture & Development Guide

This document is the single source of truth for Claude Code sessions working on GodMode. It describes the current system, its design principles, where to place new code, and how deployment works.

---

## 1. What GodMode Is

GodMode runs Claude Code sessions that ship issues, and lets you follow and steer them from the GodMode app, on a PC or a phone. It is a .NET 10 solution with one UI surface: the **MAUI app**, which hosts the React SPA in a HybridWebView, with a local relay for multi-server connectivity. The server serves no page, and no browser is its client (Section 4.4).

A server runs on a machine you own: a PC, a VM or a GitHub Codespace. Its **project roots** are directories on that machine, each with scripts and input schemas in `.godmode-root/`. **Profiles** group roots and carry shared environment variables. Both are maintained by hand on the host. You create **projects** from a root's actions. Each project is a folder with a Claude Code process working in it.

---

## 2. Solution Structure

```
GodMode.slnx
├── src/
│   ├── GodMode.Shared/            # Shared types, models, enums, hub interfaces
│   ├── GodMode.Server/            # ASP.NET SignalR server, spawns Claude processes, serves the hub and the MCP endpoint (no page)
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

`GodMode.Client.React/GodMode.Client.React.csproj`, a NoTargets project in the slnx, generates the hub types and runs `npm run build`, first running `npm ci` when `node_modules` is missing or older than `package-lock.json`. The MAUI app references it and packages `dist/` as its `wwwroot`. The server does not reference it: building or running the server builds no React and needs no npm.

### Where to Put New Code

| What you're building | Where it goes |
|---|---|
| New shared model/enum/interface | `GodMode.Shared/Models/` or `GodMode.Shared/Enums/` |
| New hub method | `GodMode.Shared/Hubs/IProjectHub.cs` + `GodMode.Server/Hubs/ProjectHub.cs` |
| New server-side service | `GodMode.Server/Services/` — register in `Program.cs` |
| New React UI component | `src/GodMode.Client.React/src/components/{Feature}/` |
| New React store action | `src/GodMode.Client.React/src/store/index.ts` |
| New TypeScript hub type | Generated: add the C# type to `GodMode.Shared` and build the client (`dotnet build src/GodMode.Client.React`, or GodMode.Maui; `tools/GodMode.TypeGen` writes `signalr/generated/hub-types.ts`). Client-only types go in `signalr/types.ts` |
| Client-side .NET abstractions | `GodMode.ClientBase/` |
| File system project utilities | `GodMode.ProjectFiles/` |
| **All UI changes** | **React only** — never in .NET projects |

---

## 3. UI Architecture: React + MAUI

### 3.1 The Rule: All UI Lives in React

React is the **single UI implementation**, and the MAUI app is its only host. There is no native .NET UI. The MAUI app is a thin shell that hosts the React SPA in a WebView and provides a local proxy server for multi-server connectivity.

When building UI features:
- Build everything in `GodMode.Client.React/`
- See a change in the Windows app: build and run `GodMode.Maui` (it rebuilds the client when its sources changed) against a running GodMode.Server; its WebView2 has DevTools enabled (F12)
- The client's tests (`npm test`) run as the app's page, with a fake shell behind `window.HybridWebView` (`src/test/appShell.ts`)

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

**There is one hosting mode.** `hostApi.ts` has no browser branch: the page's React source is embedded in the app, it connects to every server through the LocalServer relay (`skipNegotiation: true`, WebSocket only), finds its servers over the bridge (`servers.list`), and manages them there (`servers.*`). Each server's access token is in platform secure storage (Android Keystore, DPAPI, Keychain) and added by the relay; React sends the relay only its per-launch secret, and has no sign-in of its own.

### 3.3 No Browser

The server serves no page, and refuses any request with an `Origin` (Section 4.4), so a browser tab can neither load the client nor reach a hub. There is no Vite dev server either: the React dev loop is the Windows app.

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

1. **No server URLs** — React is served from `0.0.0.1` by the app and reaches servers only through the relay. Go through the helpers in `hostApi.ts`; React makes no HTTP calls to a server.

2. **SignalR connection differences** — Use `getHubUrl(serverId)` and `getHubOptions(serverId)` from `hostApi.ts`. Never hardcode hub paths.

3. **Multi-server support** — React manages connections to multiple servers. The store's `ServerConnection[]` array and `serverId` parameters exist for this reason. Don't assume a single server.

4. **No browser-only APIs without fallback** — HybridWebView is not a full browser. Avoid APIs that may not be available (e.g., `window.open`, `navigator.clipboard` may need fallbacks).

5. **Offline-capable assets** — All React assets are embedded. Don't rely on CDN-hosted fonts, icons, or scripts. Bundle everything.

6. **Test in the app** — After significant changes, check the Windows app against a running server, and consider Android (another WebView, a phone's screen).

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

| `IProjectHub` (22 methods) | |
|---|---|
| Projects | `ListProjects`, `GetStatus`, `CreateProject`, `SendInput`, `StopProject`, `ResumeProject`, `SubscribeProject`, `UnsubscribeProject`, `DeleteProject`, `RestoreProject`, `ForgetProject` |
| Prompts | `RespondToPermission`, `GetPermissionDetail`, `AnswerQuestion` |
| Attention | `GetAttention`, `MarkSeen`, `ReplyAndResume` |
| Roots | `ListProjectRoots`, `ListUnmanaged`, `AdoptFolder` |
| Profiles | `ListProfiles` |
| Utility | `CheckCommand` |

| `IProjectHubClient` (9 callbacks) |
|---|
| `OutputReceived`, `OutputBatch`, `OutputReplayComplete`, `StatusChanged`, `AttentionChanged`, `ProjectCreated`, `CreationProgress`, `ProjectDeleted`, `RootsChanged` |

**A session's ID is `{profile}/{root}/{id}`**, its id being GodMode's own, `yymmdd-{kind}-{slug}-{suffix}` (`260929-feat-left-list-k7q2`), unique within its root and the name of its state folder (4.3). Every hub method and callback that names a project takes or gives this ID (`ProjectStatus.Id`, `ProjectSummary.Id`). Clients treat it as opaque and pass it back as they received it; the server derives it from where the state folder is on every recovery. Root scripts get the id as `GODMODE_SESSION_ID` and the folder name as `GODMODE_PROJECT_FOLDER`. The session's kind (`ProjectStatus.Kind`, `ProjectSummary.Kind`), which the app shows as a label, is its create script's `kind` result, else its action's name.

**`CreateProject` returns a `CreateProjectResult`**: the project created, or, for an action that starts no session (`"session": false`, 4.2), no project and its script's `message`. Its `CreationProgress` is under the ID of the session, or of the run (`{profile}/{root}/{id}`, naming no project), and ends when the call returns.

When adding a new hub method:
1. Add to `IProjectHub` (client→server) or `IProjectHubClient` (server→client)
2. Implement in `ProjectHub`
3. Build the client (`dotnet build src/GodMode.Client.React`, or GodMode.Maui): it regenerates `signalr/generated/hub-types.ts`; commit the change
4. Wire up in `signalr/hub.ts` (GodModeHub class)
5. Expose in Zustand store if UI needs it

### 4.2 Config-Driven Project Roots

A root is a folder with a `.godmode-root/` in it, and the server's config says where its roots are (Section 6): each **scan folder** (`Roots:Scan:<key>`) makes a root of every immediate subfolder that has a `.godmode-root/`, named after it, and an **explicit root** (`Roots:Explicit:<name>:Path`) is one named folder, anywhere on disk. The server reads its sources and scans its folders again on each list of profiles or roots, on each reload of the config, and every `RootsPollSeconds` (5; a file watcher misses changes on a network drive). A server has one root per name and per folder: an explicit root wins a clash, then the first scan key in ordinal order, and each loser is logged with both paths.

**Live roots.** Once the startup's recovery has run, a root added, edited (its `profileName` included) or removed on the host, or in the instance's config file, reaches every client as `RootsChanged` (the whole lists of roots and profiles), sent only when they differ from the last ones read, so the app shows it without a reconnect. What follows for sessions:
- A root that appears has its sessions recovered as at the start, each pushed as `ProjectCreated`.
- A session is its root's by folder (`ProjectInfo.RootPath`): its delete, status, launch and restart read that folder's config and scripts, whatever the root is called now, so a root name that comes to name another folder is another root to it.
- When a session's root folder is gone, or is listed under another profile or name, a session without a claude leaves the list (`ProjectDeleted`, its files kept), and is recovered again under the ID the folder gives it now, if the folder is still a root. One whose claude runs carries on under its ID, which its MCP config carries, until claude exits. Two reads in a row must agree first, since a half-saved `config.json` reads as the default. The root stays locked while a session of the server's is in it.
- The server README (*Live roots*) has the details.

```
root-name/
├── .godmode-root/
│   ├── config.json                # Base config (profileName, prepare, delete, status, environment, claudeArgs, permissionMode, allowSkipPermissions, resumeOnRestart, resumePrompt, sharedFolder, session)
│   ├── config.{action}.json       # Per-action overlays (merged with base)
│   ├── {action}/
│   │   ├── schema.json            # Input form schema (JSON Schema)
│   │   └── create.ps1             # Action-specific creation script
│   └── scripts/
│       ├── prepare.ps1            # Shared prepare script
│       ├── delete.ps1             # Shared delete script
│       └── status.ps1             # Reports the project's pull request (optional)
└── {project-folder}/              # Working folders created from this root, each with its session
```

**Merge order**: `config.json` (base) → `config.{action}.json` (overlay). Action overlay wins on conflict.

**Actions that start no session.** `"session": false` makes an action its scripts alone: prepare and create run in the root, with its environment and inputs, and no folder is made, nothing tracked and no claude started. It is how a root provisions the host: a provisioning root's action makes a new root as a sibling in its scan folder, which live roots then list, and the server reads the roots again once it has run. The server still writes no config (5.3): the script does. Of its result file only `message` is read, which the app shows in place of opening a session; `project_path` and its checks do not apply. `sharedFolder`, `scriptsCreateFolder` or no create script with it is a config error: its create is refused, and the listing leaves that action out (logged), keeping the root's profile and other actions. The server README (*Actions that start no session*) has the details.

**Profile assignment**: `profileName` in `config.json` puts the root in that profile. An explicit root without it goes to its entry's `Profile`, and any other root to `Default`.

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

The same `.godmode` structure for everything: every session keeps its state in its working folder's `.godmode/sessions/{id}/`. A worktree is simply a folder with one session; an assistant's workspace is a folder with several (`sharedFolder`, below).

```
{root}/{project-folder}/
├── .godmode/
│   ├── .gitignore               # "*"
│   └── sessions/
│       └── {id}/                # yymmdd-{kind}-{slug}-{suffix}
│           ├── status.json      # Current state, metrics, kind
│           ├── settings.json    # The session's settings (action, permission mode, skip-permissions asked for, shared folder)
│           ├── input.jsonl      # User input log
│           ├── output.jsonl     # Claude output stream, GodMode's own
│           ├── output-generation
│           ├── session-id       # Claude's session GUID, for --resume
│           └── mcp-config.json  # While claude runs, with the session's token
└── (project files)              # Working directory for Claude
```

A session is a folder in `.godmode/sessions/` of a working folder directly inside its root, named as an id is and with a `status.json`, unless the working folder is one of the root's own folders (below), which recovery skips (`SessionState.List`, `ProjectFiles.ProjectManager.ListSessions`). `{project-folder}` is its folder name, not its ID (4.1). The old flat layout (`.godmode/status.json`) is neither read nor migrated. The server does not archive or move project folders; it moves only a deleted shared session's state, to the trash (below).

**Several sessions in one folder** is the action's to allow: `"sharedFolder": true` (default false), for an assistant root whose sessions all work in one workspace, which its create script names as `project_path` (`{root}/workspace`, strictly inside the root as every working folder is). Without it, a create whose folder another session has (tracked, or only its state on disk) is refused as in use, which keeps a worktree root from two sessions in one worktree; with it, a create may join sessions that share the folder too, never one that owns it, and a folder that sessions share takes no session that would own it. The claim a create holds from before anything is written is its ID's alone; its folder's is shared among creates that share it. Each session has its own state folder, output, MCP config and token, claude process and create log (`{root}/logs/{id}.log` and `{id}.result`, by the id it keeps). Claude Code keeps a transcript per session, so their histories do not collide; edits to the same files at once are the user's to avoid. **Deleting a session that shares its folder** runs the delete script with `GODMODE_SESSION_ID` and `GODMODE_SHARED_FOLDER=true`, then moves only its `sessions/{id}/` to the folder's `.godmode/trash/{id}/`, never the folder or its files. A session counts as sharing when its `settings.json` says it was created so, its action shares folders now, or another session is in the folder.

**The trash, and undo.** `DeleteProject` returns `DeleteProjectResult`, whose `Trashed` says a shared session's state went to the trash. `RestoreProject` moves it back and tracks the session again, `Stopped`, under the same ID, pushed as `ProjectCreated`. It fails, changing nothing, when the ID's `{profile}/{root}/` names no root listed now (the root was removed, or its profile or name changed), so a session never comes back under another ID. Nothing in the trash is recovered, and its id stays taken in its root. The server purges trash older than `TrashRetentionSeconds` (a day) at every start and every `TrashPurgeSeconds` (an hour). The app deletes such a session at once with "Deleted · Undo" for 10 seconds; a worktree's delete, which removes the folder, asks first. Folding older sessions under "N older" is the app's alone (5.3): a session folds when it has no claude, needs nothing of the user and has been quiet a week, or a day when its action is `transient`. The server README (*The trash, and folding*) has the details.

**The id** is `yymmdd-{kind}-{slug}-{suffix}` (`SessionState.Id`): the creation date, the kind, a slug of the name (lowercase `[a-z0-9-]`, 24 characters at most), and 4 random base32 characters. It is short, for Windows' path limits, and unique within its root: a create takes another suffix when a tracked session, a create in progress or a state folder on disk has it. Until the create script has run the kind is the action's name and the slug the name so far; its result (`kind`, `project_name`) gives the final id, with the same date and suffix. Claude's session GUID is not the id: it stays in `session-id`, since GodMode replaces it when a resume finds no conversation. GodMode keeps its own `output.jsonl` and reads no Claude transcript.

**`.godmode/.gitignore` is ensured on every launch** (`ProjectFolder.EnsureGitIgnore`), before the MCP config with the project token is written: created when missing, since a create script's checkout can bring a `.godmode/` without one, and given the `*` rule when it lacks it.

**A root's own folders are no project's.** `.godmode-root`, `logs` and `.archived` (a leftover) are refused as a project's folder (`ProjectFolder.ReservedFolderNames`), from the create dialog, a reuse, or a create script's `project_path`, and one found on disk with a `status.json` is not recovered. So are folder names Windows would change or not make a folder of, on every OS: a trailing dot or space (a name's trailing dots are dropped instead), and device names like `CON`, `NUL`, `COM1`. **A project's folder is strictly inside its root.** A create script's `project_path` must be inside that script's root, links followed, and not inside one of the root's own folders; the server deletes a project's folder only if it is inside a configured root by the same test, and otherwise leaves it on disk. **A project folder's files are untrusted:** its session can write them, so what reaches the `claude` command line or a link is checked when read back: `session-id` must be a GUID (anything else is no session, and the resume starts a fresh one), and a recovered pull request URL must be http(s).

**One project, one claude.** A project has at most one claude process. A create is refused while a tracked project has its ID, or its folder unless both share it (`sharedFolder`), before anything is written; create, resume, stop and delete of one project take its lock, so launches and stops come one at a time. Each session runs in a process tree of its own, off the server's console (a Job Object and a hidden console on Windows, a process group started through `setsid` on Linux). A stop interrupts claude (Ctrl+Break in its console on Windows, SIGINT to its group elsewhere), gives it `StopGracePeriodSeconds` (10) to exit, then kills the whole tree; the server's shutdown does the same for every session at once. The server README (*Sessions*) has the details and what claude was measured to honour.

**Failures keep the true state.** A project's state follows claude, whatever else fails:
- A `status.json` that cannot be saved does not fail the change: it is pushed, its events are raised, and it is saved with the next change (the failure is logged at Error).
- A line whose append to `output.jsonl` fails is tried once more on the file opened again, cut back to the last whole line, so offsets stay the file's; a line that cannot be persisted is not broadcast. The project's one consumer survives a failing item, and is started again if it faults; a subscribe or stop waiting on it fails rather than hangs.
- Pushes to clients (output, status, attention) are started in order and not waited for (`ClientSends`), so a client that stops reading holds up only its own messages, not a project or the attention list.
- The echoed user line (`--replay-user-messages`, `isReplay`) sets Running, so a result of an earlier turn handled after a send does not stand through the next turn. claude 2.1.282 folds a message sent in the middle of a turn into that turn, echoing it at its next step.
- A connection's hub calls run four at a time (`MaximumParallelInvocationsPerClient`), so a reply that waits for a resumed claude's session leaves the tab its Stop and subscribes; one connection's subscribes still run one at a time, in order.

### 4.4 Authentication

Every request needs a credential, whatever the server is bound to, loopback included. The server picks exactly one mode at startup (`AuthModeSelector` in `Auth/AuthMode.cs`):

1. **Codespace** — `CODESPACES=true`. Callers present a GitHub token owned by `GITHUB_USER`, other than the codespace's own `GITHUB_TOKEN`, which its sessions are given.
2. **API key** — anywhere else. Callers send `Authorization: Bearer <key>` (the SignalR client sends it as `access_token` on the WebSocket upgrade). The key is `Authentication:ApiKey`, else the one in the server's key file (`Auth/ApiKeyFile.cs`): generated on the first start (256 bits), printed once, owner-only, and reused on every start. The file is in the server's own data directory (`%LOCALAPPDATA%\GodMode.Server\api-key` on Windows, `~/.local/share/GodMode.Server/api-key` on Linux and in the Docker image), or `Authentication:ApiKeyFile`, and never under a scan folder or an explicit root.

**No browser** (`Auth/OriginPolicy.cs`). A request with an `Origin`, as a browser sends on every WebSocket upgrade and any request but a same-origin GET, is refused with 403 before authentication, whatever origin it names: the server's own bindings included, in Development and in a codespace too, and no setting allows one. A request with no `Origin` (the MAUI relay, the attention service, a session's claude on `/mcp`) needs its credential alone.

The server serves no page. Only `/health` is anonymous; `/`, with the key, says what the server is (`{"service":"GodMode.Server",…}`), which the app's codespace probe reads. The MCP endpoint, `/mcp`, takes a per-project token instead, and nothing else (Section 8.2). A Claude process and a root script start from an environment allowlist (`ChildEnvironment`), so the key reaches neither. Sessions still run as the server's OS user, so one that can run arbitrary commands can read the key file: the permission prompt is a gate, not a sandbox. `src/GodMode.Server/README.md` has the full binding guide.

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

Root definitions live wherever you keep them, typically a git repo you clone into a scan folder, or name as an explicit root. The server doesn't bundle root templates in its binary. `.devcontainer/godmode-server/roots/` is one such set, which the codespace copies into place.

---

## 6. Roots and Profiles in the Host's Config

A server's roots, and its profiles' descriptions and environments, are in its instance's config file (`--config <path>` or `GODMODE_CONFIG`, Section 10), on the host, beside appsettings' default. Each is a keyed map, so config sources merge entry by entry (.NET merges arrays by index). `.profiles/` is gone, and nothing reads it.

### Layout

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
  }
}
```

```
C:\Users\me\source\repos\            # Roots:Scan:repos
├── feature-root/
│   └── .godmode-root/
│       └── config.json            # "profileName": "Work" puts this root in that profile
└── bugfix-root/
    └── .godmode-root/
        └── ...
D:\notes\                          # Roots:Explicit:notes, in Private unless its config.json names a profile
```

### Key Properties

- **A root's profile** is its `config.json`'s `profileName`, else its explicit entry's `Profile`, else `Default`. A profile is listed when it has a root; one under `Profiles` alone is only settings
- **A profile's environment** (`Profiles:<name>:Environment:<VAR>`) reaches every session and root script of its roots; the root's own `environment` wins a clash. `CLAUDE_CONFIG_DIR` there pins the profile's sessions to one Claude account
- **Secrets stay in environment variables**: `Profiles__Work__Environment__JIRA_TOKEN` in the server's environment is the same setting as the file's, and needs no file
- **Adding a profile or a root** = an entry in the instance's config file, or a folder with a `.godmode-root/` in a scan folder, on the host. No hub method creates, edits or deletes one
- **Turning off an entry** an earlier source set = setting it to `""` (appsettings' `Roots:Scan:default`, say)
- **MCP servers** are not profile config: user-scoped ones live in the profile's `CLAUDE_CONFIG_DIR` (Section 8.1)
- **Git works** — a scan folder, and each root in it, can be a git repo
- **Profile env from the server's environment** — with `stripEnvVarProfile` in a root's config (or `{PROFILE}_STRIP_ENV_VAR_PROFILE=true` in the server's environment), server variables prefixed with the profile name (`MEGA_GITHUB_TOKEN`) reach that profile's sessions without the prefix (`GITHUB_TOKEN`)

---

## 7. Server Services Reference

| Service | Responsibility |
|---|---|
| `ProjectManager` | Central orchestrator — project lifecycle, profile/root snapshot, environment and launch config building |
| `ClaudeProcessManager` | Spawns Claude Code processes via `System.Diagnostics.Process`, each in a process tree of its own (`SessionProcessTree`: a Job Object on Windows, a process group on Linux), writes their output to `output.jsonl`, and stops them: interrupt, grace period, then the tree |
| `RootConfigReader` | Discovers and merges `.godmode-root/` configs |
| `ScriptRunner` | Executes cross-platform scripts (`.ps1` via `pwsh`, or `PowerShell:Executable`; `.sh` via `bash`, `.cmd`/`.bat` on Windows) |
| `RootSources` | Reads where the roots come from (`Roots:Scan`, `Roots:Explicit`) and each profile's settings (`Profiles`) from the configuration, fresh on every rebuild; `ProjectManager` finds the roots and `ApiKeyFile` keeps its file out of them |
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

The server writes the session's MCP config, its own entry alone, to `mcp-config.json` in the session's state folder, `.godmode/sessions/{id}/` (owner-only where the OS allows) and passes it with `--mcp-config`; the file is deleted when the process exits. A root or action config that still has `mcpServers`, or a profile with an `mcp/` folder, launches normally: it is logged once as a warning, and ignored.

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

For a long-running server, `dotnet publish -c Release` and run the output. Put roots in a scan folder (appsettings' `Roots:Scan:default` is `roots` under the working directory), or name them in the instance's config file (Section 6). The machine needs `claude`, `git`, `pwsh` and whatever the roots' scripts call, such as `gh`. GodMode itself needs no Node; a repo whose `.mcp.json` starts `npx` MCP servers does.

### 9.3 GitHub Codespaces

```
.devcontainer/godmode-server/
├── devcontainer.json     # Image, features, lifecycle scripts
└── roots/                # Pre-configured project roots
```

**Lifecycle**:
- `postCreateCommand`: clones `master`, publishes the server to `/opt/godmode-server`, copies `roots/` to `~/roots`, installs Claude Code
- `postStartCommand`: starts the server with `--Roots:Scan:default=roots` from `$HOME`, sets port 31337 to public

**Server URL**: `https://<codespace-name>-31337.app.github.dev/`

**Secrets**: `gh secret set ANTHROPIC_API_KEY --repos owner/repo --app codespaces`

### 9.4 Docker Image

`src/GodMode.Server/Dockerfile` builds a multi-stage image:

```
Stage 1: .NET SDK 10.0 — restores and publishes GodMode.Server
Stage 2: .NET ASP.NET 10.0 runtime — final image (`runtime` target)
Stage 3: runtime + .NET SDK — the `:sdk` tag, for sessions that build .NET code
```

No stage builds the React client: the server serves no page. The runtime image includes the published server, git, curl, Node 22 (for repos' `npx` MCP servers and JavaScript toolchains), PowerShell 7, the GitHub CLI and Claude Code, running as the non-root `godmode` user on port 31337. It sets `URLS=http://+:31337`. Run it with `-e Authentication__ApiKey=<key>`: without one it generates a key into the `godmode` user's home, which a replaced container does not keep. Mount a volume at the scan folder (`/app/roots`, appsettings' `Roots:Scan:default` from the image's working directory) to keep roots and projects across container replacements. The server manages local processes, so run one instance per workspace.

**Nothing a session runs as can change the server.** `/app` (the server) is root's and read-only to `godmode`, which owns only what the server writes: `/app/roots`, `/app/projects` (the default root when none is configured), `/data` and its home. Claude Code is installed root-owned with `npm install -g` (`/usr/bin/claude`), with self-update off (`DISABLE_AUTOUPDATER`, also in `/etc/claude-code/managed-settings.json`, since a claude process's environment is an allowlist). The server starts `claude` and `pwsh` by full path (`Claude__Executable=/usr/bin/claude`, `PowerShell__Executable=/usr/bin/pwsh`), and `/home/godmode/.local/bin`, which a session can write, is last on the `PATH`. Off Docker the two settings default to a `PATH` lookup (`claude`, `pwsh`): a codespace's claude is in `~/.local/bin`, installed by `postCreateCommand`, and the server runs as the sessions' user there anyway.

GitHub Actions (`.github/workflows/build-and-push.yml`) builds and pushes both targets to GHCR (`ghcr.io/johnjuuljensen/godmode`) on pushes to `master` that touch `src/`, `tests/` or the slnx (`latest`, `sdk`), and on a published release (plus the release tag).

### 9.5 Persistent Workspace

Every target separates the **server binary** from the **workspace data**:

```
/opt/godmode-server/          # Server binary (replaced on updates)
├── GodMode.Server.dll
└── appsettings.json          # Static config (URLs, auth, logging)

~/roots/                      # Workspace data = a scan folder (persists across updates)
└── my-root/                  # Project roots
    ├── .godmode-root/
    └── {project-folder}/     # Projects

~/.godmode-logs/              # Server logs (relative to the working directory)
```

**Key principle**: Server updates replace the binary without touching workspace data. The server reads its scan folders and explicit roots from config to find it. On startup it recovers the projects it finds there, and from then on those of any root that appears (4.2).

---

## 10. Configuration Reference

### appsettings.json (Server)

Contains only infrastructure config — not domain data:

```json
{
  "Logging": { "LogLevel": { "Default": "Information" } },
  "AllowedHosts": "*",
  "Authentication": { "ApiKey": "" },
  "Roots": { "Scan": { "default": "roots" } },
  "Urls": "http://127.0.0.1:31337"
}
```

An empty `Authentication:ApiKey` means the key file's (Section 4.4). The sources, each over the ones before: `appsettings.json`, `appsettings.{Environment}.json`, the instance's config file (`--config <path>` or `GODMODE_CONFIG`, reloaded on change), environment variables (`Authentication__ApiKey`), and the command line (`--Roots:Scan:default=...`). Config belongs to a server instance, named when it starts: there is no per-user file and no user secrets, so a server started without one runs on appsettings, with a scratch `roots` folder of its own. `Instance` (default `default`) names the server in the lock it holds on each root (`{root}/logs/server.lock`, open exclusively while it runs), and a root another live server holds is skipped (the server README has the details). Roots and profiles live in the host's config: where the roots are (`Roots:Scan`, `Roots:Explicit`) and what the profiles carry (`Profiles`) are in the instance's config file and the server's environment, and appsettings has only the scratch default. What a root is (its actions, scripts and schemas) lives in its own `.godmode-root/`. Section 6 and the server README have the keys. The API key file is refused under any scan folder or explicit root (Section 4.4).

---

## 11. Adding New Features — Checklist

When building a new feature on GodMode:

1. **Check the design principles** (Section 5). Does your feature keep its state in files on disk (a root's `.godmode-root/`, a session's `.godmode/sessions/{id}/`), read fresh? Does it avoid shadow state and in-app config authoring?

2. **Choose the right layer**:
   - Server-side logic → `GodMode.Server/Services/`
   - Shared types → `GodMode.Shared/Models/`
   - UI → React (`GodMode.Client.React/`)
   - Client .NET abstractions → `GodMode.ClientBase/`

3. **All UI work goes in React** — no native .NET UI. The MAUI app is a thin host.

4. **For new config data**: What belongs to a root goes in a file under its `.godmode-root/`. What belongs to the server instance (where roots are, what a profile carries) goes in the host's config, the instance's config file, as a keyed map so sources merge entry by entry, and never in the shipped `appsettings.json` beyond a scratch default. Adding config = adding a file or an entry; removing it = removing that.

5. **For new hub methods**: Add to the interface in Shared, implement in Server, add TypeScript types, wire into the React store.

6. **For new UI pages**: Create a component directory under `components/`, add an `activePage` variant in the store, add CSS alongside the component.

7. **Test in the app**: Verify the feature in the Windows app against a running server, and consider the app's constraints (multi-server, relay, embedded assets, Android).
