# GodMode Architecture

A conceptual guide to how the React client, server, and Claude Code work together — including root configuration, credential management, security, and sharing.

---

## System Overview

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                        REACT CLIENT (SPA)                                   │
│               Standalone web app OR embedded in MAUI HybridWebView          │
│                                                                             │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │                           Shell (main layout)                         │  │
│  │                                                                       │  │
│  │  ┌─────────┐  ┌────────────────────────────────────────────────────┐ │  │
│  │  │ Sidebar  │  │  Content Area                                     │ │  │
│  │  │          │  │                                                    │ │  │
│  │  │ Servers  │  │  List View: ProjectView (chat messages, input)    │ │  │
│  │  │  └ Profiles  │  OR                                              │ │  │
│  │  │    └ Projects │  Tile View: TileGrid (project cards overview)   │ │  │
│  │  │          │  │                                                    │ │  │
│  │  └─────────┘  └────────────────────────────────────────────────────┘ │  │
│  │                                                                       │  │
│  │  Modals: CreateProject | RootManager | McpBrowser | ProfileSettings  │  │
│  │          AddServer | EditServer | McpProfilePanel | CreateProfile     │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
│                                                                             │
│  ┌─────────────────────────────────────────┐  ┌──────────────────────────┐ │
│  │  Zustand Store (global state)           │  │  localStorage            │ │
│  │  servers[], selectedProject,            │  │  server registrations,   │ │
│  │  outputMessages[], question state,      │  │  theme, view mode,      │ │
│  │  dialog visibility, profile filter      │  │  dismissed projects     │ │
│  └──────────────┬──────────────────────────┘  └──────────────────────────┘ │
│                 │                                                           │
│  ┌──────────────▼──────────────┐                                           │
│  │  GodModeHub class           │  Can connect to MULTIPLE servers          │
│  │  (@microsoft/signalr)       │  simultaneously                           │
│  │  IProjectHub (client→server)│                                           │
│  │  IProjectHubClient (server→)│                                           │
│  └──────────────┬──────────────┘                                           │
│                 │ Optional access token (for Codespaces)                   │
└─────────────────┼───────────────────────────────────────────────────────────┘
                  │
        SignalR WebSocket(s)
        /hubs/projects
                  │
                  │  (one connection per registered server)
                  │
┌─────────────────┼───────────────────────────────────────────────────────────┐
│                 │          GODMODE SERVER (ASP.NET)                          │
│                 │     Local machine OR GitHub Codespace                      │
│                 │     Port 31337                                             │
│                 │                                                            │
│  ┌──────────────▼──────────────┐                                            │
│  │       ProjectHub            │                                            │
│  │  Hub<IProjectHubClient>     │                                            │
│  └──────────────┬──────────────┘                                            │
│                 │                                                            │
│  ┌──────────────▼──────────────┐                                            │
│  │      ProjectManager         │──────────────────────┐                     │
│  │  create, resume, stop,      │                      │                     │
│  │  roots, profiles, MCP       │                      │                     │
│  └──────────────┬──────────────┘                      │                     │
│                 │                                      │                     │
│    ┌────────────┼────────────┬────────────┐            │                     │
│    │            │            │            │            │                     │
│    ▼            ▼            ▼            ▼            ▼                     │
│ ┌────────┐ ┌────────┐ ┌────────┐ ┌──────────┐ ┌─────────────┐              │
│ │RootCfg │ │Claude  │ │Script  │ │MCP Config│ │RootInstaller│              │
│ │Reader  │ │Process │ │Runner  │ │Merger    │ │& Packager   │              │
│ │        │ │Manager │ │        │ │          │ │             │              │
│ │Scans   │ │Spawns  │ │Runs    │ │3-layer   │ │Import/export│              │
│ │roots/  │ │claude  │ │.sh/.ps1│ │merge +   │ │.gmroot pkgs │              │
│ │dirs    │ │CLI     │ │scripts │ │${VAR}    │ │             │              │
│ └────────┘ └───┬────┘ └────────┘ │expansion │ └─────────────┘              │
│                │                 └────┬─────┘                               │
│                │                      │                                     │
└────────────────┼──────────────────────┼─────────────────────────────────────┘
                 │                      │
                 │                      ▼
                 │               ┌───────────────┐
                 │               │.godmode/      │
                 │               │mcp-config.json│
                 ▼               │(per project)  │
          ┌─────────────────┐    └───────┬───────┘
          │  CLAUDE CODE    │            │
          │  (CLI Process)  │◄───────────┘
          │                 │  --mcp-config
          │ stdin: JSON msgs│
          │ stdout→output.  │
          │   jsonl         │
          │ env: API keys,  │
          │   profile vars  │
          └────────┬────────┘
                   │
                   ▼
          ┌─────────────────┐
          │  PROJECT DIR    │
          │  {root}/{id}/   │
          │                 │
          │ .godmode/       │
          │  ├─status.json  │
          │  ├─settings.json│
          │  ├─session-id   │
          │  ├─input.jsonl  │
          │  ├─output.jsonl │
          │  ├─mcp-config.  │
          │  │  json        │
          │  └─errs.txt     │
          │ (project files) │
          └─────────────────┘
```

### How the pieces connect

1. **React client** is a Vite SPA (static HTML/JS/CSS). It runs in a browser or inside a MAUI HybridWebView. It can connect to **multiple GodMode servers simultaneously**, with projects grouped by server and profile in the sidebar.

2. **Server** does all the heavy lifting: discovers root configs, merges environment/args/MCP configs from 3 layers, runs setup scripts, and spawns Claude Code as a child process. The React client has zero filesystem access — everything goes through SignalR.

3. **Claude Code** is a CLI process managed by `ClaudeProcessManager`. Communication is stdin/stdout JSON streaming (`--output-format=stream-json --input-format=stream-json`). Output is appended to `.godmode/output.jsonl` and streamed to connected clients via SignalR `OutputReceived` callbacks.

4. **MCP servers** are configured per-project by merging configs from three layers (profile + root + action), expanding `${VAR}` placeholders, and writing the result to `.godmode/mcp-config.json`.

---

## React Client — Component Architecture

```
App
└── Shell (header bar + layout)
    ├── Sidebar
    │   ├── ServerItem (per server — connection dot, name, actions)
    │   │   └── ProfileGroup (per profile)
    │   │       └── ProjectItem (per project — name, state indicator, question preview)
    │   └── Profile filter dropdown
    │
    ├── Content Area
    │   ├── ProjectView (list mode — selected project)
    │   │   ├── Header (name, profile/root labels, MCP badge, Simple/Full toggle)
    │   │   ├── ChatMessage stream (auto-scrolling)
    │   │   ├── QuestionPrompt (when Claude asks via AskUserQuestion tool)
    │   │   └── Input bar (text + send/resume/stop buttons)
    │   │
    │   └── TileGrid (tile mode — all projects)
    │       └── ProjectTile (mini preview of recent messages per project)
    │
    ├── CreateProject (modal, 2-step)
    │   ├── Step 1: Root selection grid (grouped by profile, shows actions)
    │   └── Step 2: Configuration
    │       ├── Action selector
    │       ├── Model selector (opus/sonnet/haiku)
    │       ├── MCP servers panel (effective servers + "Browse & Add")
    │       └── Dynamic form fields (parsed from action's JSON Schema)
    │
    ├── RootManager (modal, 3 tabs)
    │   ├── List: all roots grouped by profile, export buttons
    │   ├── Create: template picker → parameter form → schema editor → save
    │   │   └── AI-assisted mode: LLM generates create scripts
    │   └── Import: file upload / URL / git repo → preview → install
    │
    ├── McpBrowser (modal — search Smithery registry, view details, add to profile/root)
    ├── McpProfilePanel (modal — view/add/remove MCP servers at profile level)
    ├── ProfileSettings (modal — description + MCP server management)
    ├── CreateProfile (modal — name, description, server)
    ├── AddServer / EditServer (modals — URL, name, optional access token)
    └── Notification badge ("N waiting" when projects need input)
```

### Key UI features

- **Multi-server**: Connect to multiple GodMode servers. Projects grouped by server + profile.
- **Two view modes**: List (detailed chat) or Tile (grid overview). Toggle in header.
- **Question detection**: Parses `AskUserQuestion` tool calls from Claude output. Shows prompt with options and dismiss button. Badge count in header.
- **Dynamic forms**: Root schemas (`schema.json`) define input forms — string, multiline, boolean, enum fields — rendered automatically at project creation time.
- **MCP browser**: Search the Smithery registry, view server details/tools, configure and add to any level (profile, root, or action).
- **Dark/light theme**: System-detected, toggleable, persisted to localStorage.

### State management

- **Zustand store** holds all runtime state: servers, selected project, output messages, question state, dialog visibility, profile filters.
- **localStorage** persists: server registrations, theme preference, view mode, dismissed project questions.
- No database. All project state lives on the server's filesystem.

---

## Root Configs — Location and Structure

A **root** is a config-driven project template. It defines how Claude Code projects are created: what input forms to show, what scripts to run, what environment variables and MCP servers to inject.

```
LOCAL:      ~/roots/{root-name}/.godmode-root/
CODESPACE:  ~/roots/{root-name}/.godmode-root/
            (copied from .devcontainer/godmode-server/roots/ at codespace creation)

.godmode-root/
├── config.json              # Base config (env, claudeArgs, mcpServers, model, scripts)
├── config.{action}.json     # Per-action overlay (merged with base)
├── {action}/
│   ├── schema.json          # JSON Schema → dynamic input form in the UI
│   └── create.sh / .ps1     # Action-specific setup script
└── scripts/
    ├── prepare.sh / .ps1    # One-time root setup (e.g., clone a bare repo)
    └── delete.sh / .ps1     # Cleanup on project deletion
```

---

## Config Merging — The 3-Layer System

Configuration is merged from three layers so that **shareable structure** (committed to git) is separated from **local secrets** (never committed).

```
LAYER 1: PROFILE (local-only)            LAYER 2: ROOT (committed)         LAYER 3: ACTION (committed)
┌────────────────────────┐    ┌──────────────────────────┐    ┌──────────────────────────┐
│ appsettings.json       │    │ .godmode-root/           │    │ .godmode-root/           │
│ + ~/.godmode/          │    │   config.json            │    │   config.{action}.json   │
│   profile-overrides.json    │                          │    │                          │
│                        │    │ Contains:                │    │ Contains:                │
│ Contains:              │    │ - description            │    │ Overrides any field from │
│ - MCP servers added    │    │ - prepare/create/delete  │    │ Layer 2 for this action. │
│   at runtime via UI    │    │   script paths           │    │                          │
│ - Profile descriptions │    │ - environment with       │    │ Same merge rules apply.  │
│ - Per-profile env vars │    │   ${VAR} placeholders    │    │                          │
│   (via process env)    │    │ - claudeArgs             │    │                          │
│                        │    │ - mcpServers (structure)  │    │                          │
│ NEVER committed.       │    │ - model                  │    │                          │
│ Stays on the server    │    │                          │    │                          │
│ machine.               │    │ Safe to commit.          │    │ Safe to commit.          │
│                        │    │ No secret values.        │    │ No secret values.        │
└────────┬───────────────┘    └───────────┬──────────────┘    └───────────┬──────────────┘
         │                                │                               │
         └────────────────────────────────┼───────────────────────────────┘
                                          │
                              ┌───────────▼───────────┐
                              │       MERGE           │
                              │                       │
                              │ Scalars: overlay wins  │
                              │ Dicts:   merge per key │
                              │ Arrays:  concatenate   │
                              │ MCP:     merge, null   │
                              │          = remove      │
                              └───────────┬───────────┘
                                          │
                              ┌───────────▼───────────┐
                              │   ${VAR} EXPANSION    │
                              │                       │
                              │ Resolves from server's │
                              │ process environment.   │
                              │ Entries with unresolvable│
                              │ vars are silently dropped│
                              └───────────┬───────────┘
                                          │
                       ┌──────────────────┼──────────────────┐
                       │                  │                  │
                       ▼                  ▼                  ▼
                Process ENV         CLI args           .godmode/mcp-config.json
                (API keys,         (--model,           (passed to Claude via
                 profile vars)      --mcp-config,       --mcp-config flag)
                                    --dangerously-
                                     skip-permissions)
```

### Merge rules in detail

| Field type | Rule | Example |
|---|---|---|
| **Scalars** (string, bool) | Overlay replaces base. `null` in overlay = keep base. | `model` in action config overrides root config |
| **Dictionaries** (Environment, McpServers) | Per-key merge. Overlay wins for each key. | Action adds `MAX_TOKENS=1024`, base keeps `API_KEY=${ANTHROPIC_API_KEY}` |
| **MCP Servers** | Same as dictionaries, but setting a key to `null` **removes** it. | Action sets `"github": null` to disable a profile-level MCP server |
| **Arrays** (ClaudeArgs) | Concatenation. Base first, then overlay appended. | Root adds `["--verbose"]`, action appends `["--max-tokens", "8192"]` |

### Why this enables sharing

The key insight: **configs committed to git use `${VAR}` placeholders instead of actual secret values.** At runtime, the server resolves these from its process environment — which is set locally and never committed.

Example of a shareable `config.json`:
```json
{
  "environment": {
    "GITHUB_TOKEN": "${GITHUB_TOKEN}",
    "ANTHROPIC_API_KEY": "${ANTHROPIC_API_KEY}"
  },
  "mcpServers": {
    "github": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-github"],
      "env": { "GITHUB_TOKEN": "${GITHUB_TOKEN}" }
    }
  }
}
```

When `${GITHUB_TOKEN}` can't be resolved (e.g., the env var isn't set on the server), the entire entry is silently dropped — the project still works, just without that MCP server or env var.

### Profile prefix stripping

For multi-profile setups, environment variables can be namespaced by profile on the server machine:

```
MY_PROFILE_ANTHROPIC_API_KEY=sk-ant-...
MY_PROFILE_GITHUB_TOKEN=ghp_...
MY_PROFILE_STRIP_ENV_VAR_PROFILE=true
```

With `stripEnvVarProfile: true` in the root config, the server strips the prefix automatically:
- `MY_PROFILE_ANTHROPIC_API_KEY` → `ANTHROPIC_API_KEY` in the Claude process
- `MY_PROFILE_GITHUB_TOKEN` → `GITHUB_TOKEN` in the Claude process

This lets you run multiple profiles on the same machine without environment variable collisions.

---

## Credential Storage and Flow

### Where credentials live

| Location | What's stored | Committed? |
|---|---|---|
| Server process environment | `ANTHROPIC_API_KEY`, `GITHUB_TOKEN`, profile-prefixed vars | Never |
| `~/.godmode/inference.json` (server) | API key, provider, model for LLM-assisted root creation | Never |
| `~/.godmode/profile-overrides.json` (server) | MCP servers added via the React UI at runtime | Never |
| React client localStorage | Server URLs + optional access tokens | Never (browser-local) |
| GitHub Secrets (Codespaces) | `ANTHROPIC_API_KEY` → injected as env var in the container | Never |
| `.godmode-root/config.json` (committed) | `${VAR}` placeholders only — no actual values | Yes (safe) |

### How credentials reach Claude Code

```
Server process env          Config files with ${VAR} refs     Profile overrides
(ANTHROPIC_API_KEY,        (.godmode-root/config.json)       (~/.godmode/profile-overrides.json)
 GITHUB_TOKEN, etc.)
        │                          │                                │
        │   ┌──────────────────────┼────────────────────────────────┘
        │   │                      │
        ▼   ▼                      ▼
   ┌─────────────────────────────────────────┐
   │        MERGE + ${VAR} EXPANSION         │
   │                                         │
   │  1. Profile env (prefix-stripped + cfg) │
   │  2. Root env (config.json)             │
   │  3. Action env (config.{action}.json)  │
   │  4. Resolve ${VAR} from process env    │
   │  5. Drop entries with unresolved vars  │
   └──────────────┬──────────────────────────┘
                  │
    ┌─────────────┼──────────────────┐
    │             │                  │
    ▼             ▼                  ▼
 Process       CLI args         .godmode/mcp-config.json
 environment   (--model, etc.)  (written per-project,
 inherited                       contains expanded secrets)
 by Claude
```

### Client-side tokens

The React client stores server registrations (URL + optional access token) in **localStorage**. These tokens are used for SignalR authentication when connecting to Codespace-hosted servers. They are not encrypted — they're browser-local and scoped to the origin.

The Avalonia client (legacy) used `TokenProtector` with DPAPI encryption on Windows, but the React client relies on the browser's same-origin storage model instead.

---

## Security Analysis

### What's protected and how

| Asset | Protection | Committed? |
|---|---|---|
| `ANTHROPIC_API_KEY` | Server process env or `~/.godmode/inference.json` | Never |
| MCP server credentials | `${VAR}` placeholders in configs, resolved at runtime | Placeholders committed, values never |
| Server access tokens | React client localStorage (browser same-origin) | Never |
| Profile MCP overrides | `~/.godmode/profile-overrides.json` on server | Never |
| Per-project runtime state | `.godmode/` dir has `*` in `.gitignore` — everything excluded | Never |
| Root config structure | `.godmode-root/config.json` with `${VAR}` refs | Yes (safe) |
| Scripts | `.godmode-root/scripts/*.sh` with `$VAR` shell refs | Yes (safe) |

### Weak spots and improvements

**1. No secret scanning or validation**

There's no mechanism to warn if someone accidentally hardcodes a secret into `config.json` instead of using `${VAR}` syntax. A `config.json` with `"API_KEY": "sk-ant-actual-key"` would be committed to git silently.

> *Improvement*: Add a pre-commit hook or server-side validator that scans for patterns matching known secret formats (API keys, tokens) in `.godmode-root/` files. Warn in the React UI when creating/editing roots if a value looks like a secret rather than a `${VAR}` reference.

**2. `.godmode/mcp-config.json` contains expanded secrets at runtime**

After `${VAR}` expansion, the fully resolved MCP config (with real tokens) is written to the project directory. The `.gitignore` in `.godmode/` covers this, but if the `.gitignore` is missing or modified, secrets could leak.

> *Improvement*: Write the MCP config to a temp file outside the project tree (e.g., `/tmp/godmode-{id}/mcp-config.json`) and pass that path to `--mcp-config`. Or pass MCP config via stdin/environment instead of a file.

**3. Script execution from imported roots**

When importing a `.gmroot` package, its scripts will eventually be executed on the server machine. While SHA-256 hashes are computed and shown in the preview, there's no sandboxing — a malicious `create.sh` has full access to the server environment and all its env vars (including API keys).

> *Improvement*: Show script contents prominently in the React UI import preview. Consider running imported scripts in a container or with restricted permissions. Add a "trusted publishers" concept with signature verification for verified roots.

**4. Git import doesn't verify repository authenticity**

`PreviewFromGitAsync` clones from any URL. A typosquatted repo or MITM attack could inject malicious scripts.

> *Improvement*: Pin imports to specific commit SHAs. Warn if the source isn't a well-known host (GitHub, GitLab). Support GPG signature verification on commits.

**5. No RBAC on SignalR hub**

All authenticated clients have access to all hub methods — create/delete projects, export/install roots, restart server. There's no role-based separation.

> *Improvement*: Add role claims to the auth flow. Separate read-only viewers from operators who can create/delete projects and install roots.

**6. Codespace port visibility**

Port 31337 is set to public in Codespaces (because GitHub's `portsAttributes.visibility` field doesn't work). While the SignalR connection requires a bearer token, the public port means anyone who guesses the URL can attempt connections.

> *Improvement*: Keep ports private and require the `gh auth token` bearer approach for all connections. Add an API key requirement in the server's auth middleware even for Codespace deployments.

**7. localStorage tokens are not encrypted**

The React client stores server access tokens in plaintext localStorage. Any browser extension or XSS vulnerability could read them.

> *Improvement*: Use `sessionStorage` instead of `localStorage` (tokens cleared on tab close). Or use HttpOnly cookies for authentication where possible. Consider a browser-native credential store API.

**8. No audit logging**

There's no log of who connected, what they created/deleted, or what scripts were executed. In a multi-user Codespace scenario, this makes incident investigation difficult.

> *Improvement*: Add structured audit logging for all mutating operations (project create/delete, root install, MCP config changes, script execution).

---

## Root Sharing

### Current implementation

GodMode has a sharing system built around **`.gmroot` packages** — ZIP archives containing the `.godmode-root/` directory plus a `manifest.json` with metadata.

#### Import/export flow

```
┌──────────────────────────────────────────────────────────────────┐
│                       IMPORT SOURCES                             │
│                                                                  │
│  1. FILE UPLOAD          2. URL                3. GIT REPO       │
│  ┌──────────────┐     ┌──────────────┐     ┌──────────────┐     │
│  │ .gmroot file │     │ https://...  │     │ git clone    │     │
│  │ (ZIP bytes)  │     │ /root.gmroot │     │ --depth 1    │     │
│  └──────┬───────┘     └──────┬───────┘     │ + subpath    │     │
│         │                    │             │ + git ref    │     │
│         │                    │             └──────┬───────┘     │
│         ▼                    ▼                    ▼              │
│  PreviewFromBytes    PreviewFromUrl      PreviewFromGit         │
│                                                                  │
│         └────────────────────┼────────────────────┘              │
│                              ▼                                   │
│                    SharedRootPreview                              │
│                    ├── Manifest (name, author, version, tags)    │
│                    ├── Files (all config/schema/script content)  │
│                    └── ScriptHashes (SHA-256 for security review)│
│                              │                                   │
│               React UI shows preview for user review             │
│                              │                                   │
│                              ▼                                   │
│                    Install() → writes to roots dir               │
│                    Tracked in installed.json                     │
└──────────────────────────────────────────────────────────────────┘
```

The React client's **RootManager** modal has three tabs:
- **List**: All configured roots grouped by profile. Export button downloads `.gmroot` file.
- **Create**: Template picker (by category), parameter form, schema editor, optional AI-assisted script generation.
- **Import**: File upload, URL, or git repo (with optional subpath and git ref). Shows preview before install. Optional custom local name.

#### Export

Any root can be exported via the List tab. `RootPackager` zips `.godmode-root/` contents, auto-generates a `manifest.json` with detected platforms (from `.sh`/`.ps1` files), and downloads the result as a `.gmroot` file.

#### Manifest format

```json
{
  "name": "my-root",
  "displayName": "My Root",
  "description": "A root for...",
  "author": "mortenkremmer",
  "version": "1.0.0",
  "platforms": ["macos", "linux", "windows"],
  "tags": ["git-worktree", "issue-driven"],
  "source": "https://github.com/user/repo/roots/my-root",
  "minGodModeVersion": "0.5.0"
}
```

#### Installation tracking

Installed roots are tracked in `{rootsDir}/installed.json` with source URL, version, commit SHA, and script hashes — enabling future update detection.

### Comparison with npm/Bun packages

The `.gmroot` system is **already architecturally similar to npm/Bun** in its core mechanics:

| Feature | npm / Bun | GodMode `.gmroot` | Status |
|---|---|---|---|
| **Package format** | tarball + `package.json` | ZIP + `manifest.json` | Equivalent |
| **Install from file** | `npm install ./pkg.tgz` | File upload in Import tab | Equivalent |
| **Install from URL** | `npm install https://...` | URL field in Import tab | Equivalent |
| **Install from git** | `npm install github:user/repo` | Git repo + subpath + ref in Import tab | Equivalent |
| **Manifest metadata** | name, version, description, author, keywords, os | name, version, description, author, tags, platforms | Equivalent |
| **Pre/post install scripts** | `postinstall`, `prepare` | `prepare` script (runs on first use) | Similar |
| **Platform targeting** | `os` field in `package.json` | `platforms` in manifest (auto-detected) | Equivalent |
| **Install tracking** | `node_modules/` + `package-lock.json` | `installed.json` (version, SHA, hashes) | Similar |
| **Central registry** | `npmjs.com` / `bunx` | None | **Missing** |
| **Install by name** | `npm install express` | Must use full URL or file path | **Missing** |
| **Dependency resolution** | `package.json` dependencies, lockfiles | None | **Missing** |
| **Semantic versioning** | `^1.0.0`, `~2.3.0`, ranges | Version field exists, no resolution | **Missing** |
| **Publishing** | `npm publish` | Export to file, share manually | **Missing** |
| **Scoped packages** | `@org/package` | No namespacing | **Missing** |
| **Security audit** | `npm audit`, Snyk integration | SHA-256 script hashes at install time | Minimal |
| **Update command** | `npm update`, `npm outdated` | `installed.json` has data, no command yet | **Missing** |

### Ideas for improving root sharing

#### 1. Community registry (lightweight, like Homebrew)

Rather than running a full registry service, use a **GitHub-based index** — a well-known repo containing a `registry.json` that maps root names to git sources:

```json
{
  "git-worktree": {
    "source": "github:johnjuuljensen/godmode-roots",
    "subPath": "git-worktree",
    "description": "Git worktree-based project isolation",
    "author": "johnjuuljensen",
    "tags": ["git", "worktree"]
  }
}
```

The React UI's Import tab could add a "Browse Registry" option that fetches this index, shows a searchable list, and installs directly via the existing `PreviewFromGit` flow. Zero infrastructure needed.

#### 2. `roots.json` lockfile

A user-facing lockfile (committed to the roots directory) that pins exact versions:

```json
{
  "roots": {
    "git-worktree": {
      "source": "github:johnjuuljensen/godmode-roots",
      "subPath": "git-worktree",
      "commitSha": "abc123def456...",
      "version": "1.2.0"
    }
  }
}
```

This is partially implemented via `installed.json` but could be promoted to a first-class feature with `--frozen` install mode (refuse to install if lockfile doesn't match).

#### 3. Shorthand git syntax

Since `PreviewFromGitAsync` already supports repo + subpath + ref, add shorthand parsing:

```
github:user/repo/subpath@ref     →  repoUrl=github.com/user/repo, subPath=subpath, gitRef=ref
github:user/repo#subpath         →  alternate syntax
```

The React UI could accept this in a single text field instead of three separate inputs.

#### 4. Curated root collections (Homebrew taps model)

Organizations maintain their own root collections:

```
# Add a "tap" (just a git repo URL stored in server config)
Add tap: myorg → https://github.com/myorg/godmode-roots

# Browse roots from all taps in the UI
Available roots:
  myorg/issue-tracker    — Issue-driven development with Jira integration
  myorg/pr-reviewer      — Automated PR review workflow
  community/git-worktree — Standard git worktree isolation
```

#### 5. Root composition / inheritance

A root that extends another root, adding or overriding specific configs:

```json
{
  "extends": "github:community/git-worktree@^1.0",
  "description": "Git worktree + Jira MCP server",
  "mcpServers": {
    "jira": { "command": "npx", "args": ["-y", "mcp-jira"], "env": { "JIRA_TOKEN": "${JIRA_TOKEN}" } }
  }
}
```

This would require dependency resolution but could enable powerful composition — a team extends a community root with their specific MCP servers and scripts.

#### 6. One-click sharing via URL

Generate a shareable URL that encodes the git source:

```
https://godmode.dev/install?repo=github:user/roots&path=my-root&ref=v1.0
```

The React client could register as a handler for these URLs (or detect them via query params when loaded), opening the import preview automatically.

---

## Project Folder Structure

Each Claude Code project gets its own directory under a root, with a `.godmode/` folder for all runtime state:

```
{rootsDir}/{root-name}/{project-id}/
├── .godmode/                          # Runtime state (gitignored via * pattern)
│   ├── status.json                    # Current state (Running/Stopped/Idle/Error/WaitingInput)
│   ├── settings.json                  # Per-project settings (skipPermissions, actionName)
│   ├── session-id                     # Claude session ID (for --resume)
│   ├── input.jsonl                    # User input log
│   ├── output.jsonl                   # Claude output stream (append-only)
│   ├── mcp-config.json                # Merged + expanded MCP config (real secrets!)
│   ├── errs.txt                       # Claude stderr
│   └── .gitignore                     # Contains "*" — excludes everything in .godmode/
│
└── (project working files)            # The actual codebase Claude works on
```

### Project lifecycle

```
CREATE                        RUNNING                         STOPPED
┌─────────┐                  ┌─────────┐                     ┌─────────┐
│ User fills│   ScriptRunner  │ Claude   │   OutputReceived   │ Can      │
│ form in   │──→runs create  │ process  │──→streamed to      │ resume   │
│ React UI  │   script       │ active   │   React client     │ or delete│
│           │──→writes       │          │──→output.jsonl     │          │
│           │   mcp-config   │          │   appended         │          │
│           │──→starts       │          │                     │          │
│           │   claude CLI   │          │◄──SendInput from   │          │
└─────────┘                  └─────────┘   React client      └─────────┘
                                  │                               ▲
                                  │ Process exits                 │
                                  └───────────────────────────────┘
```
