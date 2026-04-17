# GodMode — Unified Architecture & Development Guide

This document is the single source of truth for Claude Code sessions working on GodMode. It describes the current system, its design principles, where to place new code, and how deployment works.

---

## 1. What GodMode Is

GodMode is a **Claude Autonomous Development System** — a multi-project .NET 10 solution for managing Claude Code instances across local machines and GitHub Codespaces. It provides two UI surfaces:

1. **React SPA** — served directly by GodMode.Server, accessed via browser
2. **MAUI app** — hosts the same React SPA in a HybridWebView, with a local proxy for multi-server connectivity

Users define **project roots** (templates with scripts, schemas, MCP configs) and **profiles** (groupings with shared environment and MCP servers). They create **projects** from roots, which spawn Claude Code processes that execute autonomously.

---

## 2. Solution Structure

```
GodMode.slnx
├── src/
│   ├── GodMode.Shared/            # Shared types, models, enums, hub interfaces
│   ├── GodMode.Server/            # ASP.NET SignalR server, spawns Claude processes
│   ├── GodMode.Client.React/      # React SPA (Vite + Zustand + SignalR)
│   ├── GodMode.ClientBase/        # Shared .NET client abstractions (host providers, registry)
│   ├── GodMode.Maui/              # MAUI app (Android, iOS, macOS, Windows) — hosts React
│   ├── GodMode.AI/                # Cross-platform AI abstractions
│   ├── GodMode.AI.LocalInference.Windows/  # DirectML ONNX (Windows only)
│   ├── GodMode.AI.LocalInference.Mac/      # CPU ONNX (macOS only)
│   ├── GodMode.ProjectFiles/      # File system utilities for project folders
│   ├── GodMode.Mcp/               # AWS Lambda MCP server (separate deployment)
│   └── SignalR.Proxy/             # SignalR WebSocket relay for MAUI
└── tests/
    └── GodMode.Server.Tests/
```

### Project Dependency Graph

```
GodMode.Shared  ← (no deps, shared by everything)
    ↑
GodMode.ProjectFiles
    ↑
GodMode.Server  ← GodMode.AI

GodMode.Shared
    ↑
GodMode.ClientBase  ← (host providers, server registry, token protection)
    ↑
GodMode.Maui  ← SignalR.Proxy
```

### Where to Put New Code

| What you're building | Where it goes |
|---|---|
| New shared model/enum/interface | `GodMode.Shared/Models/` or `GodMode.Shared/Enums/` |
| New hub method | `GodMode.Shared/Hubs/IProjectHub.cs` + `GodMode.Server/Hubs/ProjectHub.cs` |
| New server-side service | `GodMode.Server/Services/` — register in `Program.cs` |
| New React UI component | `src/GodMode.Client.React/src/components/{Feature}/` |
| New React store action | `src/GodMode.Client.React/src/store/index.ts` |
| New TypeScript hub type | `src/GodMode.Client.React/src/signalr/types.ts` |
| Cross-platform AI abstraction | `GodMode.AI/` |
| Platform-specific AI impl | `GodMode.AI.LocalInference.{Platform}/` |
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
| Authentication | Handled by server (Google OAuth, etc.) | Token stored in `~/.godmode/servers.json`, relayed by proxy |
| SignalR negotiate | Standard | Skipped (`skipNegotiation: true`, relay handles it) |
| Server management | Not available | Add/remove/start/stop servers via `/servers/registrations` |

### 3.4 What MAUI Developers Need to Know

The MAUI project (`GodMode.Maui/`) contains:
- `MainPage.xaml/.cs` — HybridWebView setup, injects base URL, Windows DevTools integration
- `LocalServer.cs` — HTTP listener providing REST API, SSE events, and WebSocket relay
- `MauiProgram.cs` + `ServiceCollectionExtensions.cs` — DI registration
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

1. **No server-relative URLs** — React may be served from `0.0.0.1` (MAUI) or from the server. Always use `getBaseUrl()` from `hostApi.ts` for API calls.

2. **SignalR connection differences** — Use `getHubUrl(serverId)` and `getHubOptions(serverId)` from `hostApi.ts`. Never hardcode hub paths.

3. **Multi-server support** — In MAUI, React manages connections to multiple servers. The store's `ServerConnection[]` array and `serverId` parameters exist for this reason. Don't assume a single server.

4. **No browser-only APIs without fallback** — HybridWebView is not a full browser. Avoid APIs that may not be available (e.g., `window.open`, `navigator.clipboard` may need fallbacks).

5. **Offline-capable assets** — All React assets are embedded. Don't rely on CDN-hosted fonts, icons, or scripts. Bundle everything.

6. **Test both modes** — After significant changes, verify in both browser (direct to server) and note any MAUI-specific behavior (server discovery, multi-server, auth flow).

---

## 4. Key Architectural Patterns

### 4.1 SignalR Communication (Strongly Typed)

All real-time communication uses strongly-typed SignalR:

- **`IProjectHub`** (Shared) — Client→Server methods (create project, send input, manage roots/profiles/MCP)
- **`IProjectHubClient`** (Shared) — Server→Client callbacks (output received, status changed, creation progress)
- **`ProjectHub`** (Server) — Implements `Hub<IProjectHubClient>, IProjectHub`
- **`SignalRProjectConnection`** (ClientBase) — .NET client-side, uses `TypedSignalR.Client` source generator

When adding a new hub method:
1. Add to `IProjectHub` (client→server) or `IProjectHubClient` (server→client)
2. Implement in `ProjectHub`
3. Add TypeScript type in `signalr/types.ts`
4. Wire up in `signalr/hub.ts` (GodModeHub class)
5. Expose in Zustand store if UI needs it

### 4.2 Config-Driven Project Roots

Each project root directory can contain `.godmode-root/` with:

```
root-name/
└── .godmode-root/
    ├── config.json                # Base config (prepare, delete, environment, claudeArgs)
    ├── config.{action}.json       # Per-action overlays (merged with base)
    ├── {action}/
    │   ├── schema.json            # Input form schema (JSON Schema)
    │   └── create.ps1 / .sh      # Action-specific creation script
    └── scripts/
        ├── prepare.ps1 / .sh      # Shared prepare script
        └── delete.ps1 / .sh       # Shared delete script
```

**Merge order**: `config.json` (base) → `config.{action}.json` (overlay). Action overlay wins on conflict.

**MCP server merge order**: Profile → Root → Action (three layers, later wins on conflict).

Key services:
- `RootConfigReader` — discovers and merges configs fresh on each operation (no caching, no restart needed)
- `ScriptRunner` — executes scripts with cross-platform extension resolution
- `TemplateResolver` — resolves `{fieldName}` placeholders in name/prompt templates

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

### 4.4 Authentication

The server supports exactly one auth mode (first match in `Program.cs`):
1. **Google OAuth** — if `Authentication:Google:ClientId` is configured
2. **Codespace** — if `CODESPACES=true` environment variable
3. **API Key** — if `Authentication:ApiKey` is configured
4. **None** — no auth required (default)

### 4.5 React Client Architecture

- **State**: Zustand store (`store/index.ts`) — single flat store with computed properties
- **Transport**: SignalR hub class (`signalr/hub.ts`) — manages connection, exposes typed methods
- **Components**: Organized by feature in `components/{Feature}/`
- **Styling**: CSS files per component + shared `settings-common.css`
- **No router** — navigation via `activePage` state and `selectedProject`

Active page is a union: `mcpConfig | rootManager | profileSettings | appSettings | webhookSettings | addServer | editServer | createProject`. Setting `activePage` shows the page; selecting a project clears it.

---

## 5. Design Principles — Declarative Configuration

These principles govern how configuration, state, and provisioning work in GodMode. All new features must respect them.

### 5.1 No Shadow Config Stores

Runtime changes write to the **same config files** that provisioning populates. There is one source of truth: files on disk. Never introduce a parallel config system (override stores, runtime-only state files, in-memory caches that outlive a request).

**Wrong**: A `~/.godmode/profile-overrides.json` layered on top of `appsettings.json`.
**Right**: Editing the profile's config file directly.

### 5.2 The Server Config File Layout Is the Contract

The config files on disk serve as both the provisioning input format and the runtime state format. Same shape, same files, no translation layer. If you can read the files, you understand the system state.

### 5.3 The Manifest Is the Complete Desired State

A manifest declares the full configuration of a GodMode instance — roots, profiles, MCP servers. Convergence is additive and subtractive: what's declared exists, what's not declared gets removed (with safety checks for active projects).

```
manifest → apply/converge → run → modify → export → commit manifest
```

### 5.4 Export Is First-Class

The server can serialize its current config state back into manifest format. Since runtime changes wrote to the same config files, export reads disk and emits a manifest that reproduces the current state.

### 5.5 Templates Are External

Root templates are roots like any other, living in git repos. The server doesn't bundle or manage templates in its binary.

### 5.6 Discovery Is a UI Concern

MCP server discovery (catalogs, registries) is a client/UI concern. The server consumes config; it doesn't help author it. The React client has a `connectors-catalog.ts` for curated MCP connectors — this is the right layer.

---

## 6. File-Based Profile Configuration

Profiles are moving from `appsettings.json` to a file-based structure under `.profiles/` in `ProjectRootsDir`. This follows the principle that adding a profile = adding a directory, not editing a shared config file.

### Target Layout

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
│       ├── config.json
│       └── source.json            # Provenance (git source, install date)
└── bugfix-root/
    └── .godmode-root/
        └── ...
```

### Key Properties

- **Adding a profile** = `mkdir .profiles/{name}` + write `profile.json`
- **Deleting a profile** = `rm -rf .profiles/{name}`
- **Adding an MCP server** = write a JSON file to `.profiles/{name}/mcp/`
- **Removing an MCP server** = delete the file
- **No file editing** — only create/delete whole files
- **Docker COPY works** — copy `.profiles/` into image, system is fully configured
- **Git works** — the entire `{ProjectRootsDir}` can be a git repo

### Root Provenance

Each imported root carries `.godmode-root/source.json`:

```json
{
  "git": "https://github.com/acme/roots.git",
  "ref": "v1.2",
  "path": "feature",
  "installedAt": "2026-03-31T10:00:00Z",
  "version": "1.0"
}
```

Absence = local root. This replaces any centralized `installed.json` tracking file.

---

## 7. Server Services Reference

| Service | Responsibility |
|---|---|
| `ProjectManager` | Central orchestrator — project lifecycle, profile/root listing, MCP config building |
| `ClaudeProcessManager` | Spawns Claude Code processes via `System.Diagnostics.Process` |
| `RootConfigReader` | Discovers and merges `.godmode-root/` configs |
| `ScriptRunner` | Executes cross-platform scripts (.ps1/.sh) |
| `ProfileFileManager` | CRUD on `.profiles/` directory structure |
| `RootCreator` | Creates new root directories on disk |
| `RootPackager` | Exports roots as `.gmroot` ZIP packages |
| `RootInstaller` | Installs shared roots from git/URL/bytes |
| `ConvergenceEngine` | Applies manifests — diff-and-reconcile against disk |
| `ManifestParser` | Parses manifest JSON |
| `ManifestExporter` | Exports current state as manifest |
| `StatusUpdater` | Updates `status.json` during execution |
| `TemplateResolver` | Resolves `{field}` placeholders |
| `EnvironmentExpander` | Expands `${VAR}` in config values |
| `WebhookFileManager` | Manages webhook config and token validation |
| `GodModeChatService` | Meta-management AI chat |
| `RootGenerationService` | LLM-based root config generation |
| `GitFetcher` | Git operations for root imports |

All services are registered as **singletons** in `Program.cs`.

---

## 8. MCP Server Configuration

MCP servers are configured at three levels (merge order: profile → root → action):

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

The server writes merged MCP config to a temp file and passes it via `--mcp-config {path}` to Claude Code. Environment variables in MCP config support `${VAR}` expansion from the server process environment.

The React client has a curated connector catalog (`connectors-catalog.ts`) with pre-configured MCP servers including auth templates for OAuth connectors.

---

## 9. Deployment Architecture

### 9.1 Deployment Targets

GodMode.Server is a standard ASP.NET app that runs anywhere .NET 10 runs. The three primary deployment targets:

| Target | How | Auth Mode | Use Case |
|---|---|---|---|
| **GitHub Codespaces** | DevContainer | Codespace token | Development, per-developer instances |
| **Docker (any cloud)** | Container image | Google OAuth / API Key | Production, shared team instances |
| **Local** | `dotnet run` | None / API Key | Development, testing |

### 9.2 Docker Image

The existing Dockerfile (`src/GodMode.Server/Dockerfile`) builds a multi-stage image:

```
Stage 1: Node 22 — builds React client (npm run build)
Stage 2: .NET SDK 10.0 — restores and publishes GodMode.Server
Stage 3: .NET ASP.NET 10.0 runtime — final image
```

The final image includes:
- Published server binary
- React SPA in wwwroot
- Git and curl (for root operations)
- Claude Code CLI (installed from https://claude.ai/install.sh)
- Directories: `/app/projects` (workspace), `/data` (persistent storage)
- Exposes port 31337

### 9.3 CI/CD Pipeline

GitHub Actions workflow (`.github/workflows/build-and-push.yml`):
- **Trigger**: On GitHub release publication
- **Registry**: GHCR (`ghcr.io/johnjuuljensen/godmode`)
- **Tags**: `latest` + release tag (e.g., `v1.0.0`)

### 9.4 Deploying to Azure

**Azure Container Apps** (recommended for simplicity):
```bash
# Create resource group and environment
az group create --name godmode-rg --location westeurope
az containerapp env create --name godmode-env --resource-group godmode-rg

# Deploy from GHCR
az containerapp create \
  --name godmode \
  --resource-group godmode-rg \
  --environment godmode-env \
  --image ghcr.io/johnjuuljensen/godmode:latest \
  --target-port 31337 \
  --ingress external \
  --min-replicas 1 --max-replicas 1 \
  --env-vars ANTHROPIC_API_KEY=secretref:anthropic-key \
  --secrets anthropic-key=$ANTHROPIC_API_KEY
```

Key considerations:
- **Persistent storage**: Mount an Azure Files share to `/app/projects` for workspace persistence across container restarts/updates
- **Authentication**: Configure `Authentication:Google:ClientId` and `AllowedEmail` via env vars, or use `Authentication:ApiKey`
- **Claude Code**: The Docker image includes Claude Code CLI. Ensure `ANTHROPIC_API_KEY` is set as a secret
- **Single replica**: GodMode manages local processes — it cannot scale horizontally. Always `max-replicas=1`

**Azure App Service** (alternative):
- Use a custom container deployment from GHCR
- Enable persistent storage via App Service storage mounts
- Configure `WEBSITES_PORT=31337`
- WebSocket support must be enabled (Settings → General → Web sockets: On)

### 9.5 Deploying to AWS

**AWS ECS/Fargate**:
```bash
# Create task definition with the GHCR image
# Key settings:
#   - Container port: 31337
#   - EFS volume mounted to /app/projects (for persistence)
#   - Environment: ANTHROPIC_API_KEY from Secrets Manager
#   - Task size: 1 vCPU, 2GB RAM minimum (Claude processes need memory)
#   - Desired count: 1 (no horizontal scaling)
```

**AWS App Runner** (simpler alternative):
- Create service from container image
- Port: 31337
- Instance size: 1 vCPU / 2GB minimum
- No persistent storage natively — use EFS sidecar or accept ephemeral workspace

**AWS Lambda** (GodMode.Mcp only):
The `GodMode.Mcp` project deploys separately as an AWS Lambda function:
- Runtime: .NET 8 (net8.0)
- Memory: 512 MB, timeout: 30s
- Uses DynamoDB for OAuth state storage in production
- Uses AWS Secrets Manager for API keys
- MCP endpoint at `/mcp` with GitHub OAuth authentication
- Provides tools for managing Codespaces and GitHub operations

### 9.6 GitHub Codespaces Deployment

```
.devcontainer/godmode-server/
├── devcontainer.json     # Image, features, lifecycle scripts
└── roots/                # Pre-configured project roots
```

**Lifecycle**:
- `postCreateCommand`: Clones repo, publishes server to `/opt/godmode-server`, installs Claude Code
- `postStartCommand`: Starts server on port 31337, sets port to public

**Server URL**: `https://<codespace-name>-31337.app.github.dev/`

**Secrets**: `gh secret set ANTHROPIC_API_KEY --repos owner/repo --app codespaces`

### 9.7 Persistent Workspace Architecture

All deployment targets must separate the **server binary** from the **workspace data**:

```
/opt/godmode-server/          # Server binary (replaced on updates)
├── GodMode.Server.dll
├── appsettings.json          # Static config (URLs, auth, logging)
└── wwwroot/                  # React SPA

/home/vscode/workspace/       # Workspace data (persists across updates)
├── .profiles/                # Profile definitions
│   └── default/
│       ├── profile.json
│       ├── env.json
│       └── mcp/
├── my-root/                  # Project roots
│   └── .godmode-root/
└── .godmode-logs/            # Server logs
```

**Key principle**: Server updates replace the binary without touching workspace data. The server reads `ProjectRootsDir` from config to find workspace data.

For Docker: mount a volume at the workspace path.
For Azure: use Azure Files or managed disk.
For AWS: use EFS or EBS.
For Codespaces: workspace persists with the codespace lifecycle.

### 9.8 Manifest-Based Provisioning

For production deployments, a manifest declares the complete desired state:

```yaml
roots:
  feature:
    git: https://github.com/acme/roots.git
    path: feature
    ref: v1.2
  custom:
    path: ./roots/custom
profiles:
  default:
    roots: [feature, custom]
    mcpServers:
      github:
        command: npx
        args: ["-y", "@modelcontextprotocol/server-github"]
```

On startup, `ConvergenceEngine` reads the manifest, diffs against disk, and converges:
- Creates missing roots (fetches from git if needed)
- Removes undeclared roots (with safety checks for active projects)
- Creates/updates profiles in `.profiles/`

After convergence, runtime changes go to the same files. Export reads them back into manifest format.

### 9.9 Update Flow

```
1. Build new server binary (CI/CD pushes to GHCR on release)
2. Pull new image / replace binary on target
3. Server starts, reads workspace from ProjectRootsDir
4. If manifest configured: converge (add new roots, remove deprecated ones)
5. Workspace data is untouched — projects, logs, profiles persist
```

---

## 10. Configuration Reference

### appsettings.json (Server)

Contains only infrastructure config — not domain data:

```json
{
  "Logging": { "LogLevel": { "Default": "Information" } },
  "AllowedHosts": "*",
  "Authentication": {
    "Google": { "ClientId": "...", "AllowedEmail": "..." }
  },
  "ProjectRootsDir": "roots",
  "Urls": "http://0.0.0.0:31337"
}
```

Domain data (profiles, MCP servers, roots) lives in the file tree under `ProjectRootsDir`, not in appsettings.json.

### Inference Config (~/.godmode/inference.json)

```json
{
  "api_key": "sk-ant-...",
  "provider": "anthropic",
  "model": "claude-sonnet-4-20250514",
  "max_tokens": 256,
  "temperature": 0.3
}
```

---

## 11. Adding New Features — Checklist

When building a new feature on GodMode:

1. **Check the design principles** (Section 5). Does your feature write to the same config files it reads? Can it be exported to a manifest? Does it avoid shadow state?

2. **Choose the right layer**:
   - Server-side logic → `GodMode.Server/Services/`
   - Shared types → `GodMode.Shared/Models/`
   - UI → React (`GodMode.Client.React/`)
   - Client .NET abstractions → `GodMode.ClientBase/`

3. **All UI work goes in React** — no native .NET UI. The MAUI app is a thin host.

4. **For new config data**: Represent it as a file or directory under `ProjectRootsDir`, not as a section in `appsettings.json`. Adding config = adding a file. Removing config = removing a file.

5. **For new hub methods**: Add to the interface in Shared, implement in Server, add TypeScript types, wire into the React store.

6. **For new UI pages**: Create a component directory under `components/`, add an `activePage` variant in the store, add CSS alongside the component.

7. **Test the round-trip**: Can your feature's state be exported to a manifest and re-applied to reproduce the same configuration?

8. **Test both hosting modes**: Verify the feature works in browser (direct to server) and consider MAUI constraints (multi-server, proxy, embedded assets).

---

## 12. Future Directions

### Pipeline / Multi-Agent Orchestration

Documented in `docs/pipeline_ideas.md`. Three approaches under consideration:
1. **Pipeline as first-class concept** — ordered sequence of actions with result passing
2. **Webhook chaining** — lightweight, existing infrastructure
3. **GodMode MCP server** — Claude instances can call back into GodMode to create projects, submit results, request human review

Recommended: Pipeline + GodMode MCP server combination.

### GodMode MCP Server

An MCP server that lets Claude Code instances interact with GodMode:
- `submit_result` — pass results to next pipeline step
- `create_project` — spawn sibling/child projects
- `request_human_review` — escalate decisions
- `get_project_status` — check other project states
- `update_metrics` — report custom metrics

This would live in `GodMode.Mcp/` or as a new project.

### Manifest Composition

For complex deployments, manifests may need composition (cherry-picking roots from multiple repos, environment-specific overlays). CUE or a similar configuration language could handle validation and merging if the manifest format becomes complex enough.
