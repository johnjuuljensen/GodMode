# Incremental PR Decomposition Plan for PR #71 + #72

## Context

PR #71 (MCP-support, ~5000 additions/44 files) and PR #72 (Root-configuration, ~9700 additions/146 files) are too large for review. PR #72 is a **strict superset** of PR #71 — all MCP code in #71 exists identically in #72. So we close #71 and decompose #72 into 8 incremental PRs.

Source branch: `origin/Root-configuration`
Base: `master` (at commit `8b8050c`)

---

## PR Dependency Graph

```
PR 1 (Rename Server→Host)
 ├── PR 2 (React standalone + MAUI bridge)
 │    ├── PR 7 (React MCP UI)         ← also needs PR 5
 │    └── PR 8 (React Root Manager)   ← also needs PR 6
 ├── PR 3 (Server hosts React site)   ← also needs PR 2
 └── PR 4 (Shared models + hub interfaces + ClientBase)
      ├── PR 5 (Server MCP + profiles)
      └── PR 6 (Server root templates)
```

**Merge order:** 1 → 2 & 4 (parallel) → 3 (after 1+2) & 5+6 (after 4) → 7 (after 2+5) → 8 (after 2+6)

---

## Summary Table

| PR | Title | Branch | Depends | Est. +/- | Risk |
|----|-------|--------|---------|----------|------|
| 1 | Rename Server→Host, cleanup | `incremental/01-rename-host` | — | +800/-1100 | Low-Med |
| 2 | React standalone + MAUI bridge | `incremental/02-react-standalone` | 1 | +1200/-800 | Medium |
| 3 | Server hosts React site | `incremental/03-server-hosts-react` | 1, 2 | +200 | Low |
| 4 | Shared models + hub interfaces + ClientBase | `incremental/04-shared-models-interfaces` | 1 | +2400 | Low |
| 5 | Server MCP + profiles | `incremental/05-server-mcp` | 4 | +1000 | Low-Med |
| 6 | Server root templates | `incremental/06-server-roots` | 4 | +2500 | Medium |
| 7 | React MCP UI | `incremental/07-react-mcp-ui` | 2, 5 | +1500 | Low |
| 8 | React Root Manager UI | `incremental/08-react-root-manager` | 2, 6 | +1800 | Low |

---

## PR 1: Rename Server→Host, remove unused code

**Branch:** `incremental/01-rename-host`
**Depends on:** nothing
**Est. size:** ~800 additions, ~1100 deletions
**Risk:** Low-Medium (wide rename but mechanical)

### Changes

**Shared renames (GodMode.Shared):**
- `ServerState` enum → `HostState` (file rename `ServerState.cs` → `HostState.cs`)
- `ServerInfo` → `HostInfo` (file rename)
- `ServerStatus` → `HostStatus` (file rename)
- Delete `AddServerRequest.cs`, `ServerRegistrationInfo.cs`

**ClientBase:**
- Delete `IServerProvider.cs` → replaced later in PR 4
- Delete `FileLoggerProvider.cs`, `HubConnectionFactory.cs` (unused)
- Update `GitHubCodespaceProvider.cs` references: `ServerInfo`→`HostInfo`, `ServerStatus`→`HostStatus`

**React (minimal):**
- `types.ts`: rename `ServerState`→`HostState`, `ServerInfo`→`HostInfo`, `ServerStatus`→`HostStatus`
- `store/index.ts`, `hub.ts`: fix type references

**Avalonia ViewModels** (if they reference renamed types):
- Update `ServerState`/`ServerInfo` references in view models and converters

**Solution cleanup:**
- `GodMode.slnx`: remove SignalR.Proxy if unused, add missing projects
- `Directory.Packages.props`: remove unused package versions

### Verification
- `dotnet build` succeeds
- `cd src/GodMode.Client.React && npm run build` succeeds
- `dotnet test` passes

---

## PR 2: React standalone mode — direct SignalR, MAUI bridge

**Branch:** `incremental/02-react-standalone`
**Depends on:** PR 1
**Est. size:** ~1200 additions, ~800 deletions
**Risk:** Medium (architectural change to connection model)

### Changes

**React — connection rewrite:**
- Delete `services/api.ts` (MAUI proxy REST client)
- Add `services/serverRegistry.ts` — localStorage-based server registration
- Add `host/bridge.ts`, `host/types.ts` — optional MAUI bridge abstraction
- Rewrite `signalr/hub.ts` connect method — direct SignalR to GodMode.Server
- Rewrite `store/index.ts` — `ServerConnection`→`ServerState`, index-based servers, `loadServers`/`saveServers` via localStorage, `connectServer`/`disconnectServer` direct

**React — UI updates:**
- `Shell.tsx/css` — layout changes, remove MAUI proxy deps
- `Sidebar.tsx/css` — flat server list with `ServerItem.tsx`
- Add `ServerItem.tsx` — new sidebar server component
- `AddServer.tsx`, `EditServer.tsx` — adapt to `ServerRegistration` model
- `App.tsx` — remove MAUI base URL detection
- `ProjectView.tsx/css` — delete button, layout changes
- `ProjectItem.tsx` — prop changes for server index
- `TileGrid.tsx` — adapt to new store shape
- `CreateProject.tsx/css` — style updates
- `vite.config.ts` — remove proxy configuration

**MAUI (if in scope):**
- Delete `LocalServer.cs`
- Rewrite `MainPage.xaml.cs` for bridge mode
- Add `Bridge/` folder
- Update `MauiProgram.cs`

### Verification
- `npm run build` in React project
- Manual test: React app connects directly to GodMode.Server
- `dotnet build` for MAUI project (if changed)

---

## PR 3: Server hosts React site as static files

**Branch:** `incremental/03-server-hosts-react`
**Depends on:** PR 1, PR 2
**Est. size:** ~200 additions
**Risk:** Low (additive infrastructure, no behavior change for existing clients)

### Changes

**Server middleware (Program.cs):**
- Add `app.UseDefaultFiles()` before `app.UseStaticFiles()` — serves `index.html` for `/`
- Add `app.UseStaticFiles()` — serves files from `wwwroot/`
- Add `app.MapFallbackToFile("index.html")` — SPA fallback for client-side routing
- Place static file middleware **before** auth middleware so React assets don't require auth
- Keep SignalR hub (`/hubs/projects`) and REST endpoints behind auth as before

**Build integration (GodMode.Server.csproj):**
- Add MSBuild target that runs `npm run build` in `../GodMode.Client.React/` and copies `dist/*` → `wwwroot/`
- Target runs on `AfterBuild` or `Publish`, gated by a property (e.g. `<IncludeReactClient>true</IncludeReactClient>`) so dev builds can skip it
- Add `wwwroot/` to `.gitignore` (build artifact, not committed)

**React auto-detect (hub.ts or store):**
- On startup, check if `window.location.origin` serves the app (i.e., same-origin mode)
- If server-hosted: auto-register the current origin as a server and connect to `${window.location.origin}/hubs/projects`
- If standalone (file:// or different origin or MAUI): use existing localStorage server registry
- Detection logic: check for a known marker, e.g. fetch `/health` endpoint at same origin, or check if `window.__GODMODE_BASE_URL__` is NOT set (MAUI sets it)

**Server marker endpoint (Program.cs):**
- Enhance existing `GET /` to return JSON that React can detect: `{ "name": "GodMode", "version": "...", "hosted": true }`

### Verification
- `dotnet build` succeeds
- `dotnet publish` produces `wwwroot/` with React assets
- Navigate to `http://localhost:31337` — React UI loads
- SignalR connects automatically to same-origin server
- Existing MAUI/standalone modes still work (auto-detect falls through to localStorage)

---

## PR 4: Shared models, hub interfaces, ClientBase abstractions

**Branch:** `incremental/04-shared-models-interfaces`
**Depends on:** PR 1
**Est. size:** ~2400 additions
**Risk:** Low (additive only)

### Changes

**New shared models (GodMode.Shared/Models):**
- `McpRegistry.cs` — `McpRegistrySearchResult`, `McpRegistryServer`, `McpServerDetail`, etc.
- `McpServerConfig.cs` — MCP server config record
- `RootTemplate.cs` — template metadata
- `RootPreview.cs` — file preview before writing
- `RootManifest.cs` — package metadata, `SharedRootPreview`, `InstalledRootInfo`
- `RootGenerationRequest.cs` — LLM generation spec
- `InferenceStatus.cs` — LLM availability status

**Modified shared models:**
- `CreateAction.cs` — add `McpServers` dictionary field
- `ProjectRootInfo.cs` — add `HasConfig` bool field

**New shared service:**
- `McpRegistryClient.cs` — HTTP client for Smithery registry API

**Hub interface expansion (IProjectHub.cs):**
- MCP: `SearchMcpServers`, `GetMcpServerDetail`, `AddMcpServer`, `RemoveMcpServer`, `GetEffectiveMcpServers`
- Profile: `CreateProfile`, `UpdateProfileDescription`
- Root: `ListRootTemplates`, `PreviewRootFromTemplate`, `GenerateRootWithLlm`, `CreateRoot`, `GetRootPreview`, `UpdateRoot`
- Root sharing: `ExportRoot`, `PreviewImportFromBytes/URL/Git`, `InstallSharedRoot`, `UninstallSharedRoot`
- Server mgmt: `GetInferenceStatus`, `ConfigureInferenceApiKey`, `RestartServer`

**Server stubs (ProjectHub.cs):**
- Add `NotImplementedException` stubs for all new hub methods so server compiles

**IProjectManager.cs:**
- Add matching interface method signatures

**ClientBase abstractions:**
- `IHostProvider.cs` — replaces `IServerProvider.cs`
- `IProjectConnection.cs` — unified interface with MCP + root + profile methods
- `SignalRProjectConnection.cs` — implements via SignalR
- `IProjectService.cs`, `ProjectService.cs` — profile operations
- `IHostConnectionService.cs`, `HostConnectionService.cs` — host lifecycle
- `INotificationService.cs`, `NotificationService.cs` — notification abstraction
- `GitHubCodespaceProvider.cs` — implements `IHostProvider`

**React types + hub wrappers:**
- `signalr/types.ts` — all new TypeScript interfaces
- `signalr/hub.ts` — all new hub method wrappers

### Verification
- `dotnet build` succeeds (stubs prevent compile errors)
- `npm run build` succeeds
- `dotnet test` passes

---

## PR 5: Server-side MCP with profile management

**Branch:** `incremental/05-server-mcp`
**Depends on:** PR 4
**Est. size:** ~1000 additions
**Risk:** Low-Medium

### Changes

**New server services:**
- `ProfileOverrideStore.cs` (~113 lines) — persist to `~/.godmode/profile-overrides.json`

**Modified server code:**
- `ProfileConfig.cs` — add `McpServers` dict + `Description` fields
- `RootConfigReader.cs` — merge MCP servers from config files
- `ProjectManager.cs` — implement MCP methods:
  - `SearchMcpServersAsync`, `GetMcpServerDetailAsync`
  - `AddMcpServerAsync`, `RemoveMcpServerAsync`, `GetEffectiveMcpServersAsync`
  - `CreateProfileAsync`, `UpdateProfileDescriptionAsync`
  - `BuildClaudeConfig()` — MCP merging, write `.godmode/mcp-config.json`, add `--mcp-config` arg
  - `BuildSnapshot()` — deep clone with runtime profile overrides
  - Helpers: `MergeMcpServers()`, `WriteMcpServerToConfigFile()`
- `ProjectHub.cs` — replace MCP + profile stubs with real delegations
- `Program.cs` — DI: `McpRegistryClient` (HttpClient), `ProfileOverrideStore` singleton

### Verification
- `dotnet build` && `dotnet test`
- Manual test: search Smithery registry, add MCP server to profile, create project with MCP servers

---

## PR 6: Server-side root template system + built-in templates

**Branch:** `incremental/06-server-roots`
**Depends on:** PR 4
**Est. size:** ~2500 additions (split into 6a+6b if too large)
**Risk:** Medium (AI integration, many new files)

### Changes

**New server services:**
- `RootTemplateService.cs` (~90 lines) — discover/load templates from disk
- `RootCreator.cs` (~117 lines) — validate/write root files
- `RootGenerationService.cs` (~160 lines) — LLM-assisted root generation via InferenceRouter
- `RootPackager.cs` (~147 lines) — export as .gmroot ZIP
- `RootInstaller.cs` (~230 lines) — import from URL/ZIP/git

**Modified server code:**
- `ProjectHub.cs` — replace root + server mgmt stubs with real delegations
- `ProjectManager.cs` — root methods + `GetInferenceStatus`, `ConfigureInferenceApiKey`, `RestartServer`
- `Program.cs` — DI: all root services + `AddGodModeAIServices()`
- `GodMode.Server.csproj` — add GodMode.AI project reference, template copy to output
- `appsettings.json` — template path configuration

**12 built-in template directories (Templates/):**
- `ad-hoc`, `blank`, `local-folder` — minimal (config + template.json)
- `bugfix`, `dotnet-project`, `git-clone`, `git-worktree`, `github-issue`, `monorepo`, `npm-project`, `pr-review`, `feature` — full (config, schema, cross-platform scripts)

**Sample root config:**
- `roots/github-issue/.godmode-root/` — working example

### Optional split
If >2500 lines: **PR 6a** = services only (~700 lines), **PR 6b** = templates + csproj copy rules (~1800 lines, all new files)

### Verification
- `dotnet build` && `dotnet test`
- Manual test: list templates, preview from template, create root, export/import .gmroot

---

## PR 7: React MCP UI — browser, profile panel, settings

**Branch:** `incremental/07-react-mcp-ui`
**Depends on:** PR 2 (store rewrite), PR 5 (server MCP endpoints)
**Est. size:** ~1500 additions
**Risk:** Low (all new React components)

### Changes

**New React components:**
- `components/Mcp/McpBrowser.tsx` + `.css` (~530 lines) — Smithery registry search modal
- `components/Mcp/McpProfilePanel.tsx` + `.css` (~310 lines) — per-profile MCP management
- `components/Profiles/CreateProfile.tsx` (~103 lines) — new profile form
- `components/Profiles/ProfileSettings.tsx` + `.css` (~300 lines) — edit profile settings

**Modified React:**
- `store/index.ts` — add MCP/profile UI state + actions
- `Shell.tsx` — wire MCP browser modal
- `Sidebar.tsx` — add profile settings / MCP links
- `CreateProject.tsx` — MCP servers in project creation

### Verification
- `npm run build`
- Manual test: open MCP browser, search, add server, manage profile MCP servers

---

## PR 8: React Root Manager UI

**Branch:** `incremental/08-react-root-manager`
**Depends on:** PR 2 (store rewrite), PR 6 (server root endpoints)
**Est. size:** ~1800 additions
**Risk:** Low (new React component)

### Changes

**New React components:**
- `components/Roots/RootManager.tsx` + `.css` (~1612 lines) — full root creation wizard with:
  - Template browser with accordion categories
  - Progressive wizard (template → name/profile → input → scripts → review)
  - Script editor with dual .sh/.ps1 panes
  - AI cross-platform script conversion
  - Root sharing (export/import)

**Modified React:**
- `store/index.ts` — add root manager state + actions
- `Shell.tsx` — wire root manager modal
- `Sidebar.tsx` — add root manager link
- `CreateProject.tsx/css` — root picker integration

**Misc:**
- `public/godmodelogo.png` — logo asset
- `services/questionDetection.ts` — question detection service (if not already added)

### Verification
- `npm run build`
- Manual test: open root manager, browse templates, create root from template, export/import

---

## Implementation approach

Each PR will be created by cherry-picking/extracting the relevant changes from `origin/Root-configuration` and applying them to a new branch off master (or the previous PR's branch). PR 3 (server hosting React) is new work not in the original PRs. After all 8 PRs are merged, close PR #71 and #72.
