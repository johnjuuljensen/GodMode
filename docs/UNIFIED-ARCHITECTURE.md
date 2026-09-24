# GodMode — Unified Architecture & Development Guide

This document is the single source of truth for Claude Code sessions working on GodMode. It describes the current system, its design principles, where to place new code, and how deployment works.

---

## 1. What GodMode Is

GodMode runs Claude Code sessions that ship issues, and lets you follow and steer them from a browser or a phone. It is a .NET 10 solution with two UI surfaces:

1. **React SPA** — served directly by GodMode.Server, accessed via browser
2. **MAUI app** — hosts the same React SPA in a HybridWebView, with a local proxy for multi-server connectivity

A server runs on a machine you own: a PC, a VM or a GitHub Codespace. Its **project roots** are directories on that machine, each with scripts, input schemas and MCP config in `.godmode-root/`. **Profiles** group roots and carry shared environment variables and MCP servers. You create **projects** from a root's actions. Each project is a folder with a Claude Code process working in it.

---

## 2. Solution Structure

```
GodMode.slnx
├── src/
│   ├── GodMode.Shared/            # Shared types, models, enums, hub interfaces
│   ├── GodMode.Server/            # ASP.NET SignalR server, spawns Claude processes, serves the SPA
│   ├── GodMode.ProjectFiles/      # File system utilities for project folders
│   ├── GodMode.ClientBase/        # Shared .NET client abstractions (host providers, registry)
│   ├── GodMode.Maui/              # MAUI app (Android, iOS, macOS, Windows) — hosts React
│   └── SignalR.Proxy/             # SignalR WebSocket relay for MAUI
└── tests/
    └── GodMode.Server.Tests/      # xUnit tests for GodMode.Server

Not in the slnx (npm projects):
src/
├── GodMode.Client.React/          # React SPA (Vite + Zustand + SignalR); built into the server's wwwroot
└── GodMode.McpBridge/             # stdio MCP server the server gives every Claude session (Section 8.2)
```

### Project Dependency Graph

```
GodMode.Shared  ← (no deps, shared by everything)
    ↑
GodMode.ProjectFiles
    ↑
GodMode.Server  ← GodMode.Server.Tests

GodMode.Shared
    ↑
GodMode.ClientBase  ← (host providers, server registry, token protection)
    ↑
GodMode.Maui  ← SignalR.Proxy
```

The server's and the MAUI app's builds run `npm run build` in `GodMode.Client.React` and copy its `dist/` into their `wwwroot`.

### Where to Put New Code

| What you're building | Where it goes |
|---|---|
| New shared model/enum/interface | `GodMode.Shared/Models/` or `GodMode.Shared/Enums/` |
| New hub method | `GodMode.Shared/Hubs/IProjectHub.cs` + `GodMode.Server/Hubs/ProjectHub.cs` |
| New server-side service | `GodMode.Server/Services/` — register in `Program.cs` |
| New React UI component | `src/GodMode.Client.React/src/components/{Feature}/` |
| New React store action | `src/GodMode.Client.React/src/store/index.ts` |
| New TypeScript hub type | `src/GodMode.Client.React/src/signalr/types.ts` |
| New tool for Claude sessions to call back into GodMode | `src/GodMode.McpBridge/src/index.ts` + an `/api/internal/*` endpoint in `Program.cs` |
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
│              │ HTTP/WebSocket    │
│  ┌───────────▼───────────────┐  │
│  │  LocalServer              │  │
│  │  (127.0.0.1:{port})       │  │
│  │  ├─ REST: /servers        │  │
│  │  ├─ SSE: /events          │  │
│  │  └─ WS: /?serverId=X     │  │
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

**Build integration**: The MAUI csproj has MSBuild targets that run `npm run build` and copy the React `dist/` to `Resources/Raw/wwwroot/`. HybridWebView serves these embedded files.

**Base URL injection**: On page load, MAUI injects `window.__GODMODE_BASE_URL__` pointing to the local proxy.

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
| Server discovery | Single server (the one serving the page) | Multiple servers via `/servers` REST API |
| Authentication | API key entered once, kept in that browser (Section 4.4) | Access token stored per server in `~/.godmode/servers.json`, added by the proxy when relaying |
| SignalR negotiate | Standard | Skipped (`skipNegotiation: true`, relay handles it) |
| Server management | Not available | Add/remove/start/stop servers via `/servers/registrations` |

### 3.4 What MAUI Developers Need to Know

The MAUI project (`GodMode.Maui/`) contains:
- `MainPage.xaml` + `MainPage.xaml.cs` — HybridWebView setup, injects base URL, Windows DevTools integration
- `LocalServer.cs` — HTTP listener providing REST API, SSE events, and WebSocket relay
- `MauiProgram.cs` — DI registration, using `ServiceCollectionExtensions.cs` from GodMode.ClientBase
- `Bridge/` — `HostBridge` messages between the WebView and the host
- `Platforms/` — Platform-specific entry points (minimal)

**Key services in GodMode.ClientBase/**:
- `IServerRegistryService` — manages server registrations in `~/.godmode/servers.json`
- `IServerConnectionService` — provides `IServerProvider` implementations (local folders, GitHub Codespaces)
- `ITokenProtector` — encrypts/decrypts access tokens

**SignalR.Proxy/** handles the WebSocket relay:
- `SignalRRelay` — bidirectional message relay with proper SignalR framing
- `TeeConnection` — tees server messages to a local HubConnection for typed callbacks

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

The hub is the session loop plus profiles and roots:

| `IProjectHub` (23 methods) | |
|---|---|
| Projects | `ListProjects`, `GetStatus`, `CreateProject`, `SendInput`, `StopProject`, `ResumeProject`, `SubscribeProject`, `UnsubscribeProject`, `DeleteProject`, `ArchiveProject`, `UnarchiveProject`, `ListArchivedProjects` |
| Prompts | `RespondToPermission`, `AnswerQuestion` |
| Attention | `GetAttention`, `MarkSeen`, `ReplyAndResume` |
| Roots | `ListProjectRoots` |
| Profiles | `ListProfiles`, `CreateProfile`, `DeleteProfile`, `UpdateProfileDescription` |
| Utility | `CheckCommand` |

| `IProjectHubClient` (11 callbacks) |
|---|
| `OutputReceived`, `OutputBatch`, `OutputReplayComplete`, `StatusChanged`, `AttentionChanged`, `ProjectCreated`, `CreationProgress`, `ProjectDeleted`, `ProjectArchived`, `ProjectRestored`, `ProfilesChanged` |

When adding a new hub method:
1. Add to `IProjectHub` (client→server) or `IProjectHubClient` (server→client)
2. Implement in `ProjectHub`
3. Add TypeScript type in `signalr/types.ts`
4. Wire up in `signalr/hub.ts` (GodModeHub class)
5. Expose in Zustand store if UI needs it

### 4.2 Config-Driven Project Roots

A root is a subdirectory of `ProjectRootsDir` that contains `.godmode-root/`. The server discovers roots by scanning that directory on each call, so a root added on the host shows up on the next refresh.

```
root-name/
├── .godmode-root/
│   ├── config.json                # Base config (profileName, prepare, delete, status, environment, claudeArgs, mcpServers)
│   ├── config.{action}.json       # Per-action overlays (merged with base)
│   ├── {action}/
│   │   ├── schema.json            # Input form schema (JSON Schema)
│   │   └── create.ps1             # Action-specific creation script
│   └── scripts/
│       ├── prepare.ps1            # Shared prepare script
│       ├── delete.ps1             # Shared delete script
│       └── status.ps1             # Reports the project's pull request (optional)
└── {project-id}/                  # Projects created from this root
```

**Merge order**: `config.json` (base) → `config.{action}.json` (overlay). Action overlay wins on conflict.

**Profile assignment**: `profileName` in `config.json` puts the root in that profile. Roots without it go to `Default`.

**MCP server merge order**: Profile → Root → Action (three layers, later wins on conflict).

**Pull request status**: a root's optional `status` script prints the project's pull request as JSON (`{"pullRequest": {url, number, state, review}}`, or `{}`), and the server keeps it in `ProjectStatus.PullRequest` in `status.json`. It runs on each transition to Idle or Stopped and, while the pull request is open, every 10 minutes. Only that schedule is in memory. The server parses the output strictly and knows nothing of the VCS.

Key services:
- `RootConfigReader` — discovers and merges configs fresh on each operation (no caching, no restart needed)
- `ScriptRunner` — executes scripts with cross-platform extension resolution (`.ps1` runs under `pwsh` on every OS)
- `TemplateResolver` — resolves `{fieldName}` placeholders in name/prompt templates

`src/GodMode.Server/README.md` documents every config field, the script environment and the input schema.

### 4.3 Project Folder Structure

```
{root}/{project-id}/
├── .godmode/
│   ├── status.json      # Current state, metrics
│   ├── settings.json    # Per-project settings (skip-permissions, etc.)
│   ├── input.jsonl      # User input log
│   ├── output.jsonl     # Claude output stream
│   ├── session-id       # Claude session ID for resumption
│   └── .gitignore
└── (project files)      # Working directory for Claude
```

Archiving moves the folder to `{root}/.archived/{project-id}/`.

### 4.4 Authentication

The server picks exactly one mode at startup (`AuthModeSelector` in `Auth/AuthMode.cs`):

1. **Codespace** — `CODESPACES=true`. Callers present a GitHub token owned by `GITHUB_USER`.
2. **API key** — `Authentication:ApiKey` is set. Callers send `Authorization: Bearer <key>` (the SignalR client sends it as `access_token` on the WebSocket upgrade).
3. **Loopback** — no key, and every binding is loopback. Callers need no key, but only from a loopback address, with a loopback `Host` and, when present, a loopback `Origin`.

With no key and any non-loopback binding, the server **refuses to start**. The shipped binding is `http://127.0.0.1:31337`. Binding to another address, such as the machine's Tailscale IP, needs a key. The Docker image sets `URLS=http://+:31337`, so it needs a key too.

Only `/health` and the SPA's static files are anonymous. `/api/internal/*` uses a per-project token instead (Section 8.2). `src/GodMode.Server/README.md` has the full binding guide.

### 4.5 React Client Architecture

- **State**: Zustand store (`store/index.ts`) — single flat store with computed properties
- **Transport**: SignalR hub class (`signalr/hub.ts`) — manages connection, exposes typed methods
- **Components**: Organized by feature in `components/{Feature}/`
- **Styling**: CSS files per component + shared `settings-common.css`
- **No router** — navigation via `activePage` state and `selectedProject`

Active page is a union: `profileSettings | appSettings | addServer | editServer | createProject`. Setting `activePage` shows the page; selecting a project clears it.

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

The server reads roots, actions, schemas and MCP config. It does not edit, package, import or export them. There is no in-app editor, file browser, connector catalog or manifest. The only config writes the hub makes are the profile methods (`CreateProfile`, `DeleteProfile`, `UpdateProfileDescription`), which write the same `.profiles/` files you would write by hand.

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
│   │   ├── env.json               # { "KEY": "value", ... }
│   │   └── mcp/
│   │       ├── github.json        # McpServerConfig JSON
│   │       └── filesystem.json
│   └── production/
│       ├── profile.json
│       ├── env.json
│       └── mcp/
│           └── monitoring.json
├── feature-root/
│   └── .godmode-root/
│       └── config.json            # "profileName": "production" puts this root in that profile
└── bugfix-root/
    └── .godmode-root/
        └── ...
```

### Key Properties

- **Adding a profile** = `mkdir .profiles/{name}` + write `profile.json` (or `CreateProfile` from the UI)
- **Deleting a profile** = `rm -rf .profiles/{name}`
- **Adding an MCP server** = write a JSON file to `.profiles/{name}/mcp/`
- **Removing an MCP server** = delete the file
- **Git works** — the entire `{ProjectRootsDir}` can be a git repo
- **Profile env from the server's environment** — with `stripEnvVarProfile` in a root's config (or `{PROFILE}_STRIP_ENV_VAR_PROFILE=true` in the server's environment), server variables prefixed with the profile name (`MEGA_GITHUB_TOKEN`) reach that profile's sessions without the prefix (`GITHUB_TOKEN`)

**Legacy config:** `Profiles` and `ProjectRoots` sections in `appsettings.json` are migrated into `.profiles/` once, the first time the server starts without a `.profiles/` directory. After that `.profiles/` is authoritative.

---

## 7. Server Services Reference

| Service | Responsibility |
|---|---|
| `ProjectManager` | Central orchestrator — project lifecycle, profile/root snapshot, environment and MCP config building |
| `ClaudeProcessManager` | Spawns Claude Code processes via `System.Diagnostics.Process`, writes their output to `output.jsonl` |
| `RootConfigReader` | Discovers and merges `.godmode-root/` configs |
| `ScriptRunner` | Executes cross-platform scripts (`.ps1` via `pwsh`, `.sh` via `bash`, `.cmd`/`.bat` on Windows) |
| `ProfileFileManager` | CRUD on the `.profiles/` directory structure (in `ConfigFileWriter.cs`) |
| `StatusUpdater` | Updates `status.json` during execution |
| `TemplateResolver` | Resolves `{field}` placeholders |
| `EnvironmentExpander` | Expands `${VAR}` in config values and strips profile prefixes from server env vars |
| `QuestionDetection` | Detects when Claude's turn ends in a question for the user |
| `PullRequestPoller` / `PullRequestScript` | When a root's status script runs for each project, and the strict reading of its output (owned by `ProjectManager`, not registered) |

All services are registered as **singletons** in `Program.cs`. Authentication lives in `Auth/` (`AuthModeSelector`, `GodModeAuthenticationHandler`).

---

## 8. MCP Servers

### 8.1 Configuration

MCP servers are configured at three levels (merge order: profile → root → action):

| Level | Where |
|---|---|
| Profile | `.profiles/{name}/mcp/{server}.json` |
| Root | `mcpServers` in `.godmode-root/config.json` |
| Action | `mcpServers` in `.godmode-root/config.{action}.json` |

```csharp
// GodMode.Shared/Models/McpServerConfig.cs
public record McpServerConfig(
    string? Command = null,      // stdio transport
    string[]? Args = null,
    Dictionary<string, string>? Env = null,
    string? Url = null,          // SSE transport
    Dictionary<string, string>? Headers = null);
```

**Stdio transport**: `Command` + `Args` + `Env`
**SSE transport**: `Url` + `Headers` (requires `"type": "sse"` when passed to Claude CLI)

The server writes the merged MCP config to a temp file and passes it via `--mcp-config {path}` to Claude Code. Environment variables in MCP config support `${VAR}` expansion from the server process environment.

### 8.2 The GodMode MCP Bridge

Every session also gets `godmode-bridge`, the stdio MCP server in `src/GodMode.McpBridge` (Node). The server build bundles it into one file with no dependencies, `mcp-bridge/godmode-mcp-bridge.cjs` next to the server's binaries, and a publish ships it there too. It gives Claude these tools:

| Tool | Calls | Effect |
|---|---|---|
| `godmode_submit_result` | `POST /api/internal/result` | Stores the project's structured result |
| `godmode_update_status` | `POST /api/internal/status` | Sets a custom status message shown in the UI |
| `godmode_request_human_review` | `POST /api/internal/review` | Flags the project for human attention |
| `permission_prompt` | `POST /api/internal/permission`, answered when the user answers | claude's `--permission-prompt-tool`: see below |

The server injects `GODMODE_PROJECT_ID`, `GODMODE_PROJECT_TOKEN` (per-project token that authorizes only `/api/internal/*` for that project) and `GODMODE_SERVER_URL`, an address this machine reaches the server on, picked from the addresses it is bound to (a loopback binding first, a wildcard's loopback next, else the one IP bound). It finds the bridge through the `McpBridgePath` setting (or `GODMODE_MCP_BRIDGE_PATH`), or next to its binaries, and refuses to start without it.

**Permission prompts.** claude is launched with `--permission-prompts host --permission-prompt-tool mcp__godmode-bridge__permission_prompt`, so a tool call that needs approval waits for the user instead of being denied. claude calls `permission_prompt` with `{tool_name, input, tool_use_id}`; the bridge POSTs it and holds the HTTP request open until the user answers (`node:http`, since `fetch` gives up after 5 minutes), then returns claude `{"behavior":"allow","updatedInput":{…}}` or `{"behavior":"deny","message":"…"}`. The project is `WaitingPermission` with `ProjectStatus.PendingPermission` (a server-built one-line `Summary` such as `Bash: git push origin x`), answered with the hub's `RespondToPermission`. The same flag makes claude offer `AskUserQuestion` in `--print` mode, and ask it through the same tool: that is `WaitingInput` with `PendingQuestion`, answered with `AnswerQuestion`. A request survives a client disconnect, not a server restart: the bridge's call fails and claude sees a deny. A chat message sent while one waits answers it (a single question takes it as its answer; otherwise it is a deny carrying the text).

**Attention.** `GetAttention` answers "what needs me on this server": one `AttentionItem` per project, oldest first, of kind `Permission`, `Question` (an AskUserQuestion, carried whole, or a turn that ended on `?`), `Error`, `Review` (changes requested on the project's open pull request) or `Finished` (a result the user has not seen; it and `Review` carry `PullRequestUrl`). It is derived from the status alone, and every field it reads is in `status.json` (`LastResult`, `LastResultAt`, `QuestionAt`, `SeenAt`, `PullRequest`), so a restart does not change the answer; `MarkSeen` and any reply move `SeenAt`. `AttentionChanged` pushes the whole list after a status push, only when the list differs from the last one pushed. `ReplyAndResume` answers any of it: `SendInput` to a running claude, otherwise a resume, the text, and a wait for `system/init`. claude writes nothing, not even `system/init`, until it has read its first input, so the text is sent first; the wait ends with an error if claude exits first or the `SessionStartTimeoutSeconds` setting (60) passes, and the text is sent again if the resume found no conversation and a fresh session replaced it.

---

## 9. Deployment Architecture

### 9.1 Where GodMode Runs

GodMode.Server runs on a machine you own, where Claude Code sessions can use the Claude subscriptions logged in there. Give a root its own `CLAUDE_CONFIG_DIR` in its `environment` to pin it to one subscription. There is no per-user cloud provisioning.

| Target | How | Auth Mode | Use Case |
|---|---|---|---|
| **A PC or VM** | `dotnet run`, or a published build | Loopback (same machine) or API key (reached over Tailscale or a LAN) | The main setup: sessions run on your hardware |
| **GitHub Codespaces** | `.devcontainer/godmode-server/` | Codespace token | A disposable server per developer |
| **Docker** | Image from `src/GodMode.Server/Dockerfile` | API key (required) | A containerized server on your own host |

### 9.2 On a PC or VM

```bash
# Same machine only (keyless)
dotnet run --project src/GodMode.Server/GodMode.Server.csproj

# Reachable from your phone over Tailscale: set a key and keep the loopback binding
export Authentication__ApiKey=<key>
dotnet run --project src/GodMode.Server/GodMode.Server.csproj -- \
  --urls "http://127.0.0.1:31337;http://$(tailscale ip -4):31337"
```

For a long-running server, `dotnet publish -c Release` and run the output. Put roots in `ProjectRootsDir` (default `roots` under the working directory). The machine needs `claude`, `git`, `pwsh`, Node (for the MCP bridge and `npx` MCP servers) and whatever the roots' scripts call, such as `gh`.

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

The runtime image includes the published server and SPA, git, curl, Node 22, PowerShell 7, the GitHub CLI and Claude Code, running as the non-root `godmode` user on port 31337. It sets `URLS=http://+:31337`, so run it with `-e Authentication__ApiKey=<key>`. Mount a volume at the `ProjectRootsDir` path (`/app/roots` by default) to keep roots and projects across container replacements. The server manages local processes, so run one instance per workspace.

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
│       ├── env.json
│       └── mcp/
└── my-root/                  # Project roots
    ├── .godmode-root/
    └── {project-id}/         # Projects

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

Every key can also be set as an environment variable (`Authentication__ApiKey`) or a command-line argument (`--ProjectRootsDir=...`). Domain data (profiles, MCP servers, roots) lives in the file tree under `ProjectRootsDir`, not in appsettings.json.

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
