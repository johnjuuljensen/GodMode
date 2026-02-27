# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

GodMode is a Claude Autonomous Development System - a multi-project .NET 10 solution for managing Claude Code instances across local machines and GitHub Codespaces. It provides a MAUI control plane app that communicates with remote servers via strongly-typed SignalR.

## Build Commands

```bash
# Build entire solution
dotnet build

# Build specific project
dotnet build src/GodMode.Server/GodMode.Server.csproj
dotnet build src/GodMode.Maui/GodMode.Maui.csproj

# Run server (port 31337)
dotnet run --project src/GodMode.Server/GodMode.Server.csproj

# Run MAUI app (Windows)
dotnet run --project src/GodMode.Maui/GodMode.Maui.csproj

# Run all tests
dotnet test

# Run specific test class
dotnet test src/GodMode.Maui.Tests/GodMode.Maui.Tests.csproj --filter "FullyQualifiedName~MainViewModelTests"
```

## Architecture

### Projects

- **GodMode.Shared** - Shared types, models, enums, and SignalR hub interfaces (`IProjectHub`, `IProjectHubClient`)
- **GodMode.Server** - ASP.NET SignalR server that spawns/manages Claude Code processes
- **GodMode.Maui** - Cross-platform MAUI control plane app (currently Windows-only)
- **GodMode.Avalonia** - Cross-platform Avalonia control plane app
- **GodMode.Maui.Tests** - xUnit tests using NSubstitute/FluentAssertions with linked source files
- **GodMode.ClientBase** - Shared client abstractions, services, and models used by both MAUI and Avalonia
- **GodMode.ProjectFiles** - File system utilities for project folders (status.json, JSONL streams)

### Key Patterns

**SignalR Communication (Strongly Typed)**
- `IProjectHub` (Shared) - Client→Server methods
- `IProjectHubClient` (Shared) - Server→Client callbacks (including `CreationProgress`)
- `ProjectHub` (Server) - Implements `Hub<IProjectHubClient>, IProjectHub`
- `SignalRProjectConnection` (ClientBase) - Uses `TypedSignalR.Client` source generator

**Config-Driven Project Roots**
- Each project root directory can contain a `.godmode-root.json` config file
- Config defines: input schema (JSON Schema), setup/bootstrap/teardown scripts, environment vars, Claude args
- `RootConfigReader` reads config fresh on each operation (no restart needed)
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
├── .godmode-root.json   # Root config (optional — defines creation workflow)
├── .godmode-scripts/    # Bootstrap/setup scripts (cross-platform)
│   ├── init-git.sh      # Linux version
│   └── init-git.ps1     # Windows version
└── {project-id}/        # Project folders
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

## Testing Notes

Tests use linked source files from MAUI project to avoid MAUI assembly dependencies:
- `MauiCompatibility.cs` provides stub implementations for `Shell`, `Application`, `MainThread`
- `TestBase.cs` provides pre-configured NSubstitute mocks for all services
- `FormFieldTemplateSelector.cs` is excluded from test project (MAUI-only types)
- Shell navigation throws `NullReferenceException` in tests (expected)
