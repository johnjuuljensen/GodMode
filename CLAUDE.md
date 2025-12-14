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
- **GodMode.Maui.Tests** - xUnit tests using NSubstitute/FluentAssertions with linked source files
- **GodMode.ProjectFiles** - File system utilities for project folders (status.json, JSONL streams)

### Key Patterns

**SignalR Communication (Strongly Typed)**
- `IProjectHub` (Shared) - Client→Server methods
- `IProjectHubClient` (Shared) - Server→Client callbacks
- `ProjectHub` (Server) - Implements `Hub<IProjectHubClient>, IProjectHub`
- `SignalRProjectConnection` (MAUI) - Uses `TypedSignalR.Client` source generator

**MAUI Abstractions**
- `IHostProvider` - Host environment abstraction (GitHub Codespaces, local folders)
- `IProjectConnection` - Project management operations with `IObservable<OutputEvent>` for streaming
- Implementations: `GitHubCodespaceProvider`, `LocalFolderProvider`, `SignalRProjectConnection`

**Process Management**
- `ClaudeProcessManager` uses `System.Diagnostics.Process` directly (not CliWrap) for proper stdin handling
- Processes write to `.godmode/output.jsonl`, read via `ProjectFolderWatcher`

### Project Folder Structure
```
/projects/{project-id}/
├── status.json      # Current state, metrics
├── input.jsonl      # User input log
├── output.jsonl     # Claude output stream
├── session-id       # Claude session ID for resumption
└── work/            # Working directory
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

## Testing Notes

Tests use linked source files from MAUI project to avoid MAUI assembly dependencies:
- `MauiCompatibility.cs` provides stub implementations for `Shell`, `Application`, `MainThread`
- `TestBase.cs` provides pre-configured NSubstitute mocks for all services
- Shell navigation throws `NullReferenceException` in tests (expected)
