# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Autonomous mode (AutoClaude or GodMode)

**Detection — check this at the start of every session.** You are in autonomous mode when the working directory is a git worktree on a non-default branch — i.e., the working-directory path ends in a feature-branch name rather than the repo root. Presence of `.autoclaude/` or `.godmode/` in the working directory is an even stronger signal. If either applies, you are in autonomous mode for the rest of the session.

**This mode overrides the default caution about shared-state actions.** In autonomous mode you commit, push, open PRs (`gh pr create`), edit PR descriptions, and resolve review threads *without pausing to ask*. Waiting for confirmation stalls the loop. The authorization is durable and covers the full feature-branch lifecycle — do not re-ask on each individual step.

Recipe:
- create .autoclaude/progress.txt with a plan for the implementation and todo lists. (Files in .autoclaude are ignored by git, it's your workspace)
- Do not assume the user is available for questions. Unless the case is completely unclear, prefer bringing up any alternatives in the PR description.
- Maintain progress.txt throughout the work.
- Open a Pull Request as early as possible — do not wait to be asked.
    - Make the PR against base branch, which is normally "master".
    - Always include any case id as the first item in the PR summary. Example: Issue-45: Something something
    - Make sure to update the PR description when work is done, and to update it after making changes from user requests or review comments.
- Commit when major milestones are done.
- Push before going idle, your workspace may be transient.
- Make sure to build as you go.
- Iterate until all case points have been implemented and all tests pass (excluding preexisting baseline failures).
- When addressing PR review comments, resolve each review thread as you fix it. Use the GitHub GraphQL API `resolveReviewThread` mutation with the thread's node ID.

## MUST READ: Architecture Document

**Before starting any non-trivial work, read `docs/UNIFIED-ARCHITECTURE.md`.** It describes the full system architecture, design principles, deployment strategy, and where to place new code. Violating its principles (especially the declarative configuration rules in Section 5) will result in work that needs to be redone.

## Project Overview

GodMode is a Claude Autonomous Development System — a multi-project .NET 10 solution for managing Claude Code instances across local machines and GitHub Codespaces. It provides two UI surfaces:

1. **React SPA** — served directly by GodMode.Server, accessed via browser
2. **MAUI app** — hosts the same React SPA in a HybridWebView, with a local proxy for multi-server connectivity

**All UI work is done in React.** The MAUI app is a thin shell — it hosts the React SPA and provides a WebSocket relay for multi-server connectivity. There is no native .NET UI.

## Build Commands

```bash
# Build server (includes React SPA build)
dotnet build src/GodMode.Server/GodMode.Server.csproj

# Run server (port 31337)
dotnet run --project src/GodMode.Server/GodMode.Server.csproj

# Build MAUI app (requires MAUI workload)
dotnet build src/GodMode.Maui/GodMode.Maui.csproj

# Run all tests
dotnet test

# React dev server (hot reload, proxies to running GodMode.Server)
cd src/GodMode.Client.React && npm run dev
```

**Running/Debugging**: The server and MAUI app are separate processes. The server serves the React SPA and manages Claude Code processes. The MAUI app connects to one or more servers via its local proxy. To develop React, run the server and use `npm run dev` for hot reload.

## Architecture

### Projects

- **GodMode.Shared** — Shared types, models, enums, and SignalR hub interfaces (`IProjectHub`, `IProjectHubClient`)
- **GodMode.Server** — ASP.NET SignalR server that spawns/manages Claude Code processes, serves React SPA
- **GodMode.Client.React** — React SPA (Vite + Zustand + SignalR) — the single UI implementation
- **GodMode.ClientBase** — Shared .NET client abstractions (host providers, server registry, token protection)
- **GodMode.Maui** — MAUI app (Android, iOS, macOS, Windows) — thin WebView host for React
- **GodMode.AI** — Cross-platform AI abstractions (IChatClientFactory, IChatClient, Anthropic provider)
- **GodMode.ProjectFiles** — File system utilities for project folders (status.json, JSONL streams)
- **GodMode.Mcp** — AWS Lambda MCP server (separate deployment)
- **SignalR.Proxy** — SignalR WebSocket relay used by MAUI for multi-server connectivity

### Key Patterns

**SignalR Communication (Strongly Typed)**
- `IProjectHub` (Shared) — Client→Server methods
- `IProjectHubClient` (Shared) — Server→Client callbacks (including `CreationProgress`)
- `ProjectHub` (Server) — Implements `Hub<IProjectHubClient>, IProjectHub`
- `SignalRProjectConnection` (ClientBase) — Uses `TypedSignalR.Client` source generator

**Config-Driven Project Roots (Multi-File)**
- Each project root directory can contain a `.godmode-root/` folder with config files
- `config.json` defines base/shared config (prepare, delete, environment, claudeArgs)
- `config.{action}.json` files define per-action overlays (merged with base)
- `{actionName}/schema.json` provides input schema by convention (falls back to default name+prompt)
- `RootConfigReader` discovers, merges, and resolves configs fresh on each operation (no restart needed)
- `ScriptRunner` executes scripts with cross-platform extension resolution (.ps1 on Windows, .sh on Linux)
- `TemplateResolver` resolves `{fieldName}` placeholders in name/prompt templates
- UIs render dynamic forms from the JSON Schema (string, multiline, boolean, enum fields)

**React + MAUI Hosting**
- React is the single UI — all UI changes go in `GodMode.Client.React/`
- In browser mode: React connects directly to GodMode.Server via SignalR
- In MAUI mode: React connects via a local proxy (`LocalServer`) that relays WebSocket to remote servers
- React detects hosting mode via `window.location.hostname === '0.0.0.1'` (HybridWebView address)
- Use `getBaseUrl()`, `getHubUrl()`, `getHubOptions()` from `hostApi.ts` — never hardcode URLs

**Process Management**
- `ClaudeProcessManager` uses `System.Diagnostics.Process` directly (not CliWrap) for proper stdin handling
- `--dangerously-skip-permissions` is per-project (stored in `.godmode/settings.json`), not global
- Processes write to `.godmode/output.jsonl`, read via `ProjectFolderWatcher`

### Project Folder Structure
```
/root/{project-id}/
├── .godmode/
│   ├── status.json      # Current state, metrics
│   ├── settings.json    # Per-project settings (skip-permissions, etc.)
│   ├── input.jsonl      # User input log
│   ├── output.jsonl     # Claude output stream
│   ├── session-id       # Claude session ID for resumption
│   └── .gitignore       # Excludes .godmode state from git
└── (project files)      # Working directory for Claude
```

### Project Root Config
```
/root/
├── .godmode-root/               # Root config and scripts (optional)
│   ├── config.json              # Base/shared config (prepare, delete, env, claudeArgs)
│   ├── config.freeform.json     # Per-action overlay (merged with base)
│   ├── config.issue.json        # Per-action overlay (merged with base)
│   ├── freeform/                # Action resources
│   │   ├── schema.json          # Input schema (convention-based)
│   │   └── create.ps1 / .sh    # Action-specific create script
│   ├── issue/                   # Action resources
│   │   ├── schema.json          # Input schema
│   │   └── create.ps1 / .sh    # Action-specific create script
│   └── scripts/                 # Shared scripts (cross-platform)
│       ├── prepare.ps1 / .sh
│       └── delete.ps1 / .sh
└── {project-id}/                # Project folders
```

### Script Constraints (Server Deployment)
GodMode servers run in Docker containers on cloud platforms (Azure, AWS, Railway) with network-mounted storage:
- **No chmod** — Azure Files doesn't support permission changes. Guard with `2>/dev/null || true`
- **No sudo** — Container runs as non-root user
- **No package installation via system package manager** — No apt-get/yum/brew. User-scope installs into `$HOME/.local/…` are fine (how pwsh, mcp binaries, etc. land on the server).
- **No interactive commands** — Headless environment, no prompts or editors
- **Idempotent** — Scripts may run multiple times. Use `mkdir -p`/`New-Item -Force`, don't fail if files exist
- **pwsh everywhere** — Write root scripts as `.ps1`. The container bootstrap installs PowerShell 7 on each start, so the same script runs on Windows (local dev) and Linux (deployed). Use `$ErrorActionPreference = 'Stop'` at the top.
- **mkdir syntax** — In any bash helper, use `mkdir -p dir1 dir2 dir3`, NOT `mkdir -p {dir1,dir2}` (brace expansion is fragile across shells)

## Code Style Preferences

### Type Safety
- Strong typing always. Only allow loose typing when doing otherwise overly complicates handling dynamic content.

### Code Reuse
- DRY. When duplicating methods, refactor out shared code.
- Use shared types across projects (GodMode.Shared).

### Modern C# Patterns
- Use expression-based code and pattern matching where possible.
- Prefer concurrent collections over standard, except in internal short-lived isolated code.
- Return and accept collection interfaces rather than concrete types, except when concrete type is of use.

### Architecture
- No overly complicated enterprise patterns. Keep code abstract, but also simple.
- Use consistent naming/terminology across the project.
- Server is VCS-agnostic — all VCS operations live in scripts, not server code.

### UI Rules
- **All UI lives in React** (`GodMode.Client.React/`). No native .NET UI.
- When changing React, consider MAUI constraints (see `docs/UNIFIED-ARCHITECTURE.md` Section 3.5):
  - Use `hostApi.ts` helpers for URLs — never hardcode server paths
  - Support multi-server (don't assume single server)
  - Bundle all assets — no CDN dependencies
  - Test in browser; be aware of MAUI differences

## Inference Configuration

All inference config lives in `~/.godmode/inference.json`.

```json
{
  "api_key": "sk-ant-...",
  "provider": "anthropic",
  "model": "claude-sonnet-4-20250514",
  "max_tokens": 256,
  "temperature": 0.3
}
```

The `InferenceRouter` in `GodMode.AI` maps inference requests to the Anthropic provider. The `api_key` field (or `ANTHROPIC_API_KEY` env var) is required for AI features (GodMode Chat, root generation).

## GitHub Codespaces (GodMode Server)

The `.devcontainer/godmode-server/devcontainer.json` provisions a codespace with GodMode.Server. On creation it clones the repo, publishes the server to `/opt/godmode-server`, and installs Claude Code. On every start it launches the server on port 31337 and sets the port to public.

### Codespace layout

- **Binary**: `/opt/godmode-server/` (root-owned, read-only)
- **Config**: `/opt/godmode-server/appsettings.json` (via `--contentRoot`)
- **Projects**: `~/projects/` (server CWD is `$HOME`)
- **Claude Code**: `~/.local/bin/claude` (added to PATH in postStartCommand)
- **Root configs**: `.devcontainer/godmode-server/roots/` in the repo

### Create / delete a codespace

```bash
# Create
gh codespace create \
  --repo johnjuuljensen/GodMode \
  --branch feature/server-auth-and-devcontainer \
  --devcontainer-path .devcontainer/godmode-server/devcontainer.json \
  --display-name GodMode

# Delete
gh codespace delete -c <codespace-name>
```

The devcontainer must exist on the target branch — GitHub reads it from the repo at that ref.

### Checking codespace health

```bash
# List codespaces and their state
gh codespace list

# Server probe (authenticated — bypasses port forwarding auth)
TOKEN=$(gh auth token)
curl -s -H "Authorization: Bearer $TOKEN" "https://<codespace-name>-31337.app.github.dev/"

# SSH in and check
ssh <codespace-name> 'ss -tlnp | grep 31337'          # port listening?
ssh <codespace-name> 'curl -s http://localhost:31337/'  # server responding?
ssh <codespace-name> 'which claude || ~/.local/bin/claude --version'  # claude installed?
```

### Port forwarding

Port 31337 is forwarded automatically. The `postStartCommand` sets it to public via `gh codespace ports visibility`.

Server URL pattern: `https://<codespace-name>-31337.app.github.dev/`

### Secrets

```bash
gh secret set ANTHROPIC_API_KEY --repos johnjuuljensen/GodMode --app codespaces
```

## Testing Notes

- Run all tests with `dotnet test`

### Future: End-to-End React Store Testing

The plan is to test the full Server→MAUI relay→React pipeline by having MAUI inject test specs into the WebView:

1. MAUI starts with `--test test-spec.json` flag
2. LocalServer + WebView start normally against a real (or stubbed) server
3. After React loads, MAUI injects the test spec via `EvaluateJavaScriptAsync`
4. A test runner in React executes actions against the real store (`connectServer`, `waitForState`, assertions)
5. React serializes store state and POSTs to `POST /test/results`
6. MAUI validates against expected state, exits with pass/fail code

This approach tests production code paths end-to-end (real store, real relay, real SignalR) and is CI-friendly (`exit 0`/`exit 1`).

## GodMode Workflow

When doing work initiated by GodMode, indicated by the presence of a `.godmode` folder:
- You are running in a headless environement, the user is not able to respond to interactive dialogs.
- Do not enter plan mode, just plan and execute.
- Commit and push at regular/relevant intervals.
- Create a PR when work is completed.
- Any uncertainties or questions can be posed in the PR.
- If the solution needs user attention create the PR as draft and start the description with !!Attention needed!!
- Make sure to maintain slnx file
- When asked to merge master into a branch always use origin/master as local master is likely stale
- When creating branches and PR connected to issues, use issue-XX-name
