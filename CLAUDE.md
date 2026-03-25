# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

GodMode is a Claude Autonomous Development System - a multi-project .NET 10 solution for managing Claude Code instances across local machines and GitHub Codespaces. It provides an Avalonia control plane app that communicates with remote servers via strongly-typed SignalR.

This is a **cross-platform application** targeting Windows, macOS, Android, and iOS.

## Build Commands

```bash
# Build entire solution
dotnet build

# Build specific project
dotnet build src/GodMode.Server/GodMode.Server.csproj
dotnet build src/GodMode.Avalonia.Desktop/GodMode.Avalonia.Desktop.csproj

# Run server (port 31337)
dotnet run --project src/GodMode.Server/GodMode.Server.csproj

# Run Desktop app
dotnet run --project src/GodMode.Avalonia.Desktop/GodMode.Avalonia.Desktop.csproj

# Build Android app (requires Android workload)
dotnet build src/GodMode.Avalonia.Android/GodMode.Avalonia.Android.csproj

# Run all tests
dotnet test
```

**Running/Debugging**: The server and Avalonia app are separate processes — the server is **not** embedded in the Avalonia app. To debug, start both projects concurrently (e.g., "Multiple Startup Projects" in Visual Studio, or compound launch configurations in VS Code).

## Architecture

### Projects

- **GodMode.Shared** - Shared types, models, enums, and SignalR hub interfaces (`IProjectHub`, `IProjectHubClient`)
- **GodMode.Server** - ASP.NET SignalR server that spawns/manages Claude Code processes
- **GodMode.Avalonia** - Shared UI class library (views, view models, converters, styles) — referenced by Desktop and Android heads
- **GodMode.Avalonia.Desktop** - Desktop entry point (WinExe, Program.cs, platform project refs)
- **GodMode.Avalonia.Android** - Android entry point (MainActivity, AndroidManifest)
- **GodMode.ClientBase** - Shared client abstractions, services, and models used by the Avalonia app
- **GodMode.ProjectFiles** - File system utilities for project folders (status.json, JSONL streams)
- **GodMode.AI** - Cross-platform AI abstractions (IChatClientFactory, IChatClient, tools, tool call parsing, AIConfig, Anthropic provider)
- **GodMode.AI.LocalInference.Windows** - Windows DirectML ONNX local inference (Phi-4 mini)
- **GodMode.AI.LocalInference.Mac** - macOS CPU ONNX local inference

### Key Patterns

**SignalR Communication (Strongly Typed)**
- `IProjectHub` (Shared) - Client→Server methods
- `IProjectHubClient` (Shared) - Server→Client callbacks (including `CreationProgress`)
- `ProjectHub` (Server) - Implements `Hub<IProjectHubClient>, IProjectHub`
- `SignalRProjectConnection` (ClientBase) - Uses `TypedSignalR.Client` source generator

**Config-Driven Project Roots (Multi-File)**
- Each project root directory can contain a `.godmode-root/` folder with config files
- `config.json` defines base/shared config (prepare, delete, environment, claudeArgs)
- `config.{action}.json` files define per-action overlays (merged with base)
- `{actionName}/schema.json` provides input schema by convention (falls back to default name+prompt)
- `RootConfigReader` discovers, merges, and resolves configs fresh on each operation (no restart needed)
- `ScriptRunner` executes scripts with cross-platform extension resolution (.ps1 on Windows, .sh on Linux)
- `TemplateResolver` resolves `{fieldName}` placeholders in name/prompt templates
- UIs render dynamic forms from the JSON Schema (string, multiline, boolean, enum fields)

**Client Abstractions**
- `IHostProvider` - Host environment abstraction (GitHub Codespaces, local folders)
- `IProjectConnection` - Project management operations with `IObservable<OutputEvent>` for streaming
- `FormField` / `FormFieldParser` - Dynamic form model parsed from JSON Schema for UI rendering
- Implementations: `GitHubCodespaceProvider`, `LocalFolderProvider`, `SignalRProjectConnection`

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

### Cross-Platform Rules
This is a cross-platform application (Windows, macOS, Android, iOS). Follow these rules strictly:

- **All platform-specific code lives in platform projects** (e.g., `GodMode.AI.LocalInference.Mac`). App projects like `GodMode.Avalonia` must only depend on cross-platform abstractions.
- **Define common interfaces in cross-platform projects** (`GodMode.AI`) and implement them per-platform. Register via `IPlatformServiceRegistrar` for automatic discovery, or use `TryAddSingleton` for fallback defaults.
- **No `#if` preprocessor directives or `<Compile Remove>` in app projects.** All app code must compile on every platform. If you need platform-specific behavior, define an interface, implement it per-platform, and inject it.
- **Pragmas and conditionals are a last resort** — only use them when there is genuinely no other way (e.g., suppressing an unavoidable compiler warning on a no-op interface implementation). Never use them to gate features.
- **Platform projects use conditional csproj patterns** for stub builds on non-target platforms: `<Compile Remove="**/*.cs" />` with an OS condition so they produce empty assemblies elsewhere.
- **Null/no-op implementations** (`NullChatClient`, etc.) ensure the app runs gracefully on platforms where a capability isn't yet available.

## Inference Configuration

All inference config lives in `~/.godmode/inference.json`.

### Config File Structure (`~/.godmode/inference.json`)
```json
{
  "api_key": "sk-ant-...",
  "provider": "anthropic",
  "model": "claude-sonnet-4-20250514",
  "phi4_model_path": "~/.godmode/models/phi-4-mini-instruct-onnx-gpu",
  "max_tokens": 256,
  "temperature": 0.3,
  "tiers": {
    "Light": { "provider": "anthropic" },
    "Medium": { "provider": "anthropic" },
    "Heavy": { "provider": "anthropic" }
  }
}
```

### Key Fields

| Field | Owner | Description |
|-------|-------|-------------|
| `api_key` | AIConfig | API key for remote providers (env var `ANTHROPIC_API_KEY` as fallback) |
| `provider` | AIConfig | Active provider: `"anthropic"`, `"directml"`, `"cpu"`, `"none"` (default: auto-detect) |
| `model` | AIConfig | Model ID for remote providers (e.g., `"claude-sonnet-4-20250514"`) |
| `phi4_model_path` | AIConfig | Path to Phi-4-mini ONNX model dir (must contain `genai_config.json`) |
| `max_tokens` | AIConfig | Max generation tokens (default: 256) |
| `temperature` | AIConfig | Sampling temperature (default: 0.3) |
| `tiers` | AIConfig | Optional tier→provider mapping (auto-detected if absent) |

### Inference Tier System

The `InferenceRouter` maps task tiers (Light/Medium/Heavy) to execution providers via `IChatClientFactory`:

- **Auto-detect mode** (no `tiers` section): If `api_key` or `ANTHROPIC_API_KEY` env var is set, uses Anthropic for all tiers. Otherwise falls back to local DirectML/CPU if a model path is configured.
- **Explicit tiers**: Add a `tiers` section to override auto-detection. Provider values: `"anthropic"`, `"directml"`, `"cpu"`, `"auto"`, `"none"`.
- **Fallback chain**: If a tier's provider fails, falls back through anthropic→directml→cpu→any loaded client.
- **Client sharing**: If multiple tiers map to the same provider, they share one `IChatClient` instance.
- **NPU is disabled**: NPU/VitisAI support has been removed.

### IChatClientFactory Pattern

Inference providers implement `IChatClientFactory` and are registered as keyed DI services:
- `AnthropicChatClientFactory` ("anthropic") — remote inference via Anthropic API (lives in GodMode.AI)
- `Phi4ChatClientFactory` ("directml") — local ONNX via DirectML (lives in GodMode.AI.LocalInference.Windows)
- `OnnxChatClientFactory` ("cpu") — local ONNX on CPU (lives in GodMode.AI.LocalInference.Mac)

The `InferenceRouter` resolves factories from DI by provider key, calls `CreateAsync()`, and stores the resulting `IChatClient` instances.

### Model Downloads

Run `scripts/download-models.ps1` to download local models to `~/.godmode/models/`:
- **Phi-4-mini** (DirectML GPU): `phi-4-mini-instruct-onnx-gpu/` — requires `genai_config.json`

### Platform Service Discovery

Platform-specific implementations (AI inference) are registered via `IPlatformServiceRegistrar` discovered at startup by scanning `GodMode.*.dll` assemblies. Platform assemblies are preloaded from the output directory before scanning to avoid lazy-loading gaps.

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

### SSH access

An SSH config wildcard (`~/.ssh/config`) enables passwordless SSH to any GodMode codespace using the ssh-agent:

```
Host godmode-*
    User vscode
    ProxyCommand C:\Program Files\GitHub CLI\gh.exe cs ssh -c %n --stdio -- -i C:\Users\JJJ\.ssh\id_ed25519
    UserKnownHostsFile=/dev/null
    StrictHostKeyChecking no
    LogLevel quiet
    ControlMaster auto
    IdentityFile C:\Users\JJJ\.ssh\id_ed25519
```

Then: `ssh <codespace-name>` (e.g., `ssh godmode-p6gpgg6v7539xpx`).

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

Port 31337 is forwarded automatically. The `postStartCommand` sets it to public via `gh codespace ports visibility`. The `portsAttributes.visibility` field in devcontainer.json does NOT work (GitHub backlog since 2022, see github/community#4068).

The authenticated probe (`Bearer` token from `gh auth token`) bypasses both private-port login redirects and public-port interstitial warnings, so port visibility is not critical for the Avalonia client.

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

Server configs should be stubbable so tests don't depend on live codespaces:
- **Stubbed codespace provider**: fake servers with various states (won't start, will start, already running) to test the full lifecycle UI without GitHub API calls
- **Local server**: a real GodMode.Server instance for testing the happy path (connect, list projects, create, subscribe output)
- **Stubbed config**: injected server registrations so tests don't touch `~/.godmode/servers.json`

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
