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
dotnet build src/GodMode.Avalonia/GodMode.Avalonia.csproj

# Run server (port 31337)
dotnet run --project src/GodMode.Server/GodMode.Server.csproj

# Run Avalonia app
dotnet run --project src/GodMode.Avalonia/GodMode.Avalonia.csproj

# Run all tests
dotnet test
```

**Running/Debugging**: The server and Avalonia app are separate processes — the server is **not** embedded in the Avalonia app. To debug, start both projects concurrently (e.g., "Multiple Startup Projects" in Visual Studio, or compound launch configurations in VS Code).

## Architecture

### Projects

- **GodMode.Shared** - Shared types, models, enums, and SignalR hub interfaces (`IProjectHub`, `IProjectHubClient`)
- **GodMode.Server** - ASP.NET SignalR server that spawns/manages Claude Code processes
- **GodMode.Avalonia** - Cross-platform Avalonia control plane app
- **GodMode.ClientBase** - Shared client abstractions, services, and models used by the Avalonia app
- **GodMode.ProjectFiles** - File system utilities for project folders (status.json, JSONL streams)
- **GodMode.AI** - Cross-platform AI abstractions (ILanguageModel, tools, tool call parsing, AIConfig)
- **GodMode.AI.LocalInference.Windows** - Windows DirectML ONNX local inference (Phi-4 mini)
- **GodMode.AI.LocalInference.Mac** - macOS CPU ONNX local inference
- **GodMode.Voice** - Cross-platform voice/speech abstractions and orchestration (AssistantService, ISpeechRecognizer, VoiceConfig)
- **GodMode.Voice.Windows** - Windows native STT/TTS (Windows.Media.SpeechRecognition)
- **GodMode.Voice.Mac** - macOS speech stubs (placeholder for AVSpeechSynthesizer/SFSpeechRecognizer)

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

- **All platform-specific code lives in platform projects** (e.g., `GodMode.Voice.Windows`, `GodMode.AI.LocalInference.Mac`). App projects like `GodMode.Avalonia` must only depend on cross-platform abstractions.
- **Define common interfaces in cross-platform projects** (`GodMode.AI`, `GodMode.Voice`) and implement them per-platform. Register via `IPlatformServiceRegistrar` for automatic discovery, or use `TryAddSingleton` for fallback defaults.
- **No `#if` preprocessor directives or `<Compile Remove>` in app projects.** All app code must compile on every platform. If you need platform-specific behavior, define an interface, implement it per-platform, and inject it.
- **Pragmas and conditionals are a last resort** — only use them when there is genuinely no other way (e.g., suppressing an unavoidable compiler warning on a no-op interface implementation). Never use them to gate features.
- **Platform projects use conditional csproj patterns** for stub builds on non-target platforms: `<Compile Remove="**/*.cs" />` with an OS condition so they produce empty assemblies elsewhere.
- **Null/no-op implementations** (`NullLanguageModel`, `NullSpeechRecognizer`, etc.) ensure the app runs gracefully on platforms where a capability isn't yet available.
- **Configuration classes that share a file** (e.g., `AIConfig` and `VoiceConfig` both read/write `~/.godmode/inference.json`) must merge on save — never overwrite the other class's keys.

## Inference Configuration

All inference and voice config lives in `~/.godmode/inference.json`. This file is shared between `AIConfig` (AI keys) and `VoiceConfig` (voice keys) — each class owns its keys and merges on save.

### Config File Structure (`~/.godmode/inference.json`)
```json
{
  "phi4_model_path": "~/.godmode/models/phi-4-mini-instruct-onnx-gpu",
  "npu_model_path": "~/.godmode/models/qwen2.5-0.5b-instruct-onnx",
  "max_tokens": 256,
  "temperature": 0.3,
  "whisper_model_path": "~/.godmode/models/whisper/ggml-base.bin",
  "speech_language": "en-US",
  "prefer_offline_stt": true,
  "tiers": {
    "Light": { "provider": "npu", "model_path": null },
    "Medium": { "provider": "directml" },
    "Heavy": { "provider": "directml" }
  }
}
```

### Key Fields

| Field | Owner | Description |
|-------|-------|-------------|
| `phi4_model_path` | AIConfig | Path to Phi-4-mini ONNX model dir (must contain `genai_config.json`) |
| `npu_model_path` | AIConfig | Path to Qwen2.5-0.5B ONNX model dir (must contain `tokenizer.json`) |
| `max_tokens` | AIConfig | Max generation tokens (default: 256) |
| `temperature` | AIConfig | Sampling temperature (default: 0.3) |
| `tiers` | AIConfig | Optional tier→provider mapping (auto-detected if absent) |
| `whisper_model_path` | VoiceConfig | Path to Whisper GGML model **file** (e.g., `ggml-base.bin`) |
| `speech_language` | VoiceConfig | Speech recognition language (default: `en-US`) |
| `prefer_offline_stt` | VoiceConfig | Prefer offline STT engine (default: true) |

### Inference Tier System

The `InferenceRouter` maps task tiers (Light/Medium/Heavy) to execution providers (npu/directml/cpu):

- **Auto-detect mode** (no `tiers` section): Router infers from flat config fields. NPU+DirectML → Light=npu, Medium/Heavy=directml. DirectML only → all tiers=directml.
- **Explicit tiers**: Add a `tiers` section to override auto-detection. Provider values: `"npu"`, `"directml"`, `"cpu"`, `"auto"`, `"none"`.
- **Fallback chain**: If a tier's provider fails, falls back through npu→directml→cpu→any loaded model.
- **Model sharing**: If multiple tiers map to the same provider, they share one model instance.

### Model Downloads

Run `scripts/download-models.ps1` to download all models to `~/.godmode/models/`:
- **Phi-4-mini** (DirectML GPU): `phi-4-mini-instruct-onnx-gpu/` — requires `genai_config.json`
- **Qwen2.5-0.5B** (NPU): `qwen2.5-0.5b-instruct-onnx/` — requires `tokenizer.json`
- **Whisper base** (STT): `whisper/ggml-base.bin` — single GGML file

### Platform Service Discovery

Platform-specific implementations (speech, AI inference) are registered via `IPlatformServiceRegistrar` discovered at startup by scanning `GodMode.*.dll` assemblies. Platform assemblies are preloaded from the output directory before scanning to avoid lazy-loading gaps.

## Testing Notes

- Run all tests with `dotnet test`

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
