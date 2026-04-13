# GodMode — Claude Autonomous Development System

GodMode is a platform for managing autonomous Claude Code instances at scale. It turns Claude Code from a single-user CLI tool into a managed fleet of AI developers — each with their own workspace, tools, and instructions — orchestrated through a web UI or API.

One server, many projects. Each project is an isolated Claude Code session with its own files, MCP tools, and conversation history. Projects are created from **roots** (templates that define what Claude should do), organized into **profiles** (groups with shared configuration), and triggered manually, via **schedules**, or through **webhooks**.

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        Browser / MAUI App                       │
│                                                                 │
│   ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌──────────┐      │
│   │ Project  │  │ Roots &  │  │Connectors│  │ GodMode  │      │
│   │  Views   │  │ Profiles │  │  (MCP)   │  │   Chat   │      │
│   └────┬─────┘  └────┬─────┘  └────┬─────┘  └────┬─────┘      │
│        └──────────────┴──────────────┴──────────────┘           │
│                           │ SignalR                              │
└───────────────────────────┼─────────────────────────────────────┘
                            │
┌───────────────────────────┼─────────────────────────────────────┐
│                    GodMode Server (.NET)                        │
│                                                                 │
│   ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌──────────┐      │
│   │ Project  │  │  Root    │  │ Schedule │  │ Webhook  │      │
│   │ Manager  │  │ Config   │  │ Manager  │  │ Handler  │      │
│   └────┬─────┘  └──────────┘  └──────────┘  └──────────┘      │
│        │                                                        │
│   ┌────┴─────────────────────────────────────────────┐         │
│   │              Claude Code Processes                │         │
│   │  ┌─────────┐  ┌─────────┐  ┌─────────┐          │         │
│   │  │Project A│  │Project B│  │Project C│  ...      │         │
│   │  │+ MCP    │  │+ MCP    │  │+ MCP    │          │         │
│   │  └─────────┘  └─────────┘  └─────────┘          │         │
│   └──────────────────────────────────────────────────┘         │
│                                                                 │
│   Persistent Storage: /app/projects                             │
│   ├── .profiles/{name}/mcp/ schedules/ oauth/                  │
│   ├── {root}/{project}/.godmode/ (state, output, session)      │
│   └── .archived/ .webhooks/                                    │
└─────────────────────────────────────────────────────────────────┘
                            │
         ┌──────────────────┼──────────────────┐
         │                  │                  │
   ┌─────┴─────┐    ┌──────┴──────┐    ┌─────┴─────┐
   │  Google   │    │  Atlassian  │    │   Azure   │
   │ Workspace │    │  Jira MCP   │    │  MCP (30  │
   │ MCP (22   │    │  (18 tools) │    │   tools)  │
   │  tools)   │    │             │    │           │
   └───────────┘    └─────────────┘    └───────────┘
   mcp.ingodmode.xyz    mcp.atlassian.com    mcp.ingodmode.xyz/azure
```

**React SPA** — the single UI, served by the server or hosted in a MAUI WebView for desktop/mobile. Zustand state management, SignalR real-time transport.

**GodMode Server** — ASP.NET application that manages Claude Code processes, serves the UI, handles OAuth, and exposes a SignalR hub with 40+ methods for project lifecycle, root management, and configuration.

**Claude Code Processes** — each project spawns a Claude Code CLI process with `--print` mode, session persistence, and injected MCP servers. Output is streamed in real-time via JSONL.

**MCP Servers** — external tool servers that give Claude access to services. Configured per-profile, injected at project start. Hosted MCPs at `mcp.ingodmode.xyz` for Google, Azure, and Grafana. Third-party MCPs for Jira, GitHub, Slack, etc.

**Persistent Storage** — all state lives on disk under `ProjectRootsDir`. Profiles, projects, schedules, webhooks, OAuth tokens, and archived projects survive container restarts and image updates.

---

## Feature List

### Project Management
- Create projects from configurable root templates with dynamic input forms
- Real-time Claude output streaming (thinking, tool calls, results)
- Stop, resume, and delete projects with session persistence
- Archive projects (preserves data, frees sidebar) with search and restore
- Duplicate project name handling (reuse folder or auto-suffix)
- Auto-open project after creation
- Tile view and list view with grouping by profile, root, status, or recency

### Roots & Actions
- Root templates define how projects are created (config, scripts, schemas)
- Multiple actions per root — each with its own form, prompt, and scripts
- JSON Schema-based dynamic input forms (text, multiline, boolean, enum)
- Name and prompt templates with `{fieldName}` placeholder resolution
- Prepare, create, and delete scripts with cross-platform support
- Config overlay merging (base + per-action)
- Root generation from natural language via LLM
- Import roots from ZIP/gmroot files, Git repos, or manual creation
- Export, copy, and share roots across instances

### MCP Connectors
- **Google Workspace** — Gmail, Calendar, Drive, Meet (22 tools, hosted MCP OAuth)
- **Jira & Confluence** — issues, sprints, projects, pages (18 tools, Atlassian OAuth)
- **Azure** — AKS, PostgreSQL, Storage, Key Vault, DNS, users, billing (30 tools, Microsoft OAuth)
- **Grafana** — dashboards, alerts, Loki logs, Prometheus queries (9 tools, header auth)
- **GitHub** — repositories, PRs, issues, code search (PAT auth)
- **Slack** — channels, messages, users, reactions (bot token + manifest)
- **Vanta** — compliance frameworks, security tests (client credentials)
- **Fireflies** — meeting transcripts and summaries (API key)
- **Local Files** — scoped directory access (path only)
- Three-level MCP merge: profile → root → action
- Connector catalog with setup guides, OAuth flows, and app manifests

### Schedules
- Cron-based project creation triggers with visual builder
- Frequency modes: Minutes, Hourly, Daily, Weekly, Monthly, Advanced
- Human-readable schedule descriptions
- Project name and prompt fields with `{date}`, `{time}`, `{datetime}` placeholders
- Reuse Project mode — same folder re-run for recurring checks
- Enable/disable without deletion
- Next run prediction display

### Webhooks
- HTTP-triggered project creation (`POST /webhook/{keyword}`)
- Bearer token authentication with regeneration
- Payload-to-input mapping via JSON Path
- Static input defaults
- Enable/disable per webhook

### GodMode Chat
- AI meta-management assistant with 20+ tools
- Create and manage profiles, roots, MCP servers, projects
- Configure webhooks and schedules via conversation
- Generate root configurations from natural language
- Full system state awareness (profiles, roots, projects, schedules)

### File Browser
- Browse project files, root configs, and profile settings
- Filter pills: Projects, Roots, Profiles, All
- Upload files from local machine, download files
- Inline text editor with save
- Create folders, delete files/directories
- Root/project badges and level hints

### Authentication
- Google OAuth via auth proxy (no client credentials needed on server)
- Microsoft OAuth for Azure connector
- Atlassian OAuth for Jira
- API key and GitHub Codespace token modes
- Single allowed email gate per server instance
- Cookie-based sessions with logout

### Deployment
- **Azure Container Apps** — one-click deploy via deploy.ingodmode.xyz
- **AWS ECS/Fargate** — provisioning scripts and GitHub Actions
- **Railway** — provisioning via GraphQL API
- Auto-update: push to master → Docker build → rollout to all instances
- Persistent storage via Azure Files / Railway Volumes
- Per-user isolation (separate container per user)

---

## Use Cases

### 1. Autonomous Jira Ticket Implementation
Create a root that takes a Jira ticket ID, fetches ticket details via the Jira MCP, analyzes requirements, creates a feature branch, implements the solution, writes tests, and submits a PR — all without human intervention.

### 2. Daily Code Review Bot
Schedule a daily project that uses the GitHub MCP to find open PRs, reviews the code changes, leaves comments with suggestions, and posts a summary to Slack. Runs every morning at 9am using the reuse-project mode.

### 3. Infrastructure Monitoring & Alerting
Schedule a project every 15 minutes that uses the Azure MCP to check resource health, queries Grafana for anomalies, and uses the Slack MCP to alert the team if anything looks wrong. Reuses the same project folder each run.

### 4. Meeting Summary Pipeline
When a meeting ends, a Fireflies webhook triggers a GodMode project that fetches the transcript, summarizes key decisions, extracts action items, creates Jira tickets for each, and posts the summary to the team's Slack channel.

### 5. Compliance Report Generation
A weekly schedule triggers a project using the Vanta MCP to pull compliance status, generates a report with findings and remediation suggestions, and emails it to the security team via Gmail MCP.

### 6. Multi-Repository Refactoring
Create a root with a "refactor" action that takes a description of the change needed across multiple repos. Claude uses GitHub MCP to fork, make changes, run tests, and create PRs across all targeted repositories.

### 7. Customer Support Triage
A webhook connected to a support system creates a GodMode project for each new ticket. Claude analyzes the issue, searches knowledge bases via Local Files MCP, drafts a response, and either auto-replies or escalates to a human via the MCP bridge's `request_human_review` tool.

### 8. Database Migration Assistant
A root with a "migrate" action that takes a migration description. Claude uses the Azure MCP to inspect the current PostgreSQL schema, generates migration scripts, tests them against a staging database, and creates a PR with the changes.

### 9. Documentation Generator
A schedule runs weekly to scan project repositories via GitHub MCP, identifies undocumented or poorly documented code, generates documentation, and creates PRs. Uses Google Drive MCP to update a shared documentation index.

### 10. Release Manager
A root with "release" action that orchestrates a full release: creates a release branch via GitHub MCP, runs the test suite, generates release notes from commit history, updates changelogs, creates a GitHub release, and notifies the team via Slack.

### 11. Security Vulnerability Scanner
A daily schedule that uses GitHub MCP to scan repositories for dependency vulnerabilities, checks Vanta compliance status, queries Azure resources for security misconfigurations, and generates a consolidated security report.

### 12. Onboarding Automation
When a new team member is added (webhook from HR system), Claude sets up their development environment: creates Azure resources, configures access permissions, generates personalized documentation, creates Jira onboarding tickets, and sends a welcome message via Slack.

---

## Developer Guide

### Running Locally

```bash
# Build and run the server (includes React SPA build)
dotnet run --project src/GodMode.Server/GodMode.Server.csproj

# Or for React hot reload development
cd src/GodMode.Client.React && npm run dev
# (keep the server running in another terminal)
```

The server runs at `http://localhost:31337`. In development mode (`npm run dev`), React runs on port 5173 with hot reload and proxies API calls to the server.

### Project Structure

```
src/
├── GodMode.Shared/          # Shared types, models, hub interfaces
├── GodMode.Server/          # ASP.NET server, Claude process management
├── GodMode.Client.React/    # React SPA (Vite + Zustand + SignalR)
├── GodMode.AI/              # Inference abstractions (Anthropic provider)
├── GodMode.ProjectFiles/    # File system utilities
├── GodMode.McpBridge/       # MCP server for Claude→GodMode communication
├── GodMode.Maui/            # Desktop/mobile app (hosts React in WebView)
└── SignalR.Proxy/           # WebSocket relay for MAUI multi-server
```

### Adding a New Feature

1. **Shared types** → `GodMode.Shared/Models/`
2. **Hub method** → `IProjectHub.cs` (interface) + `ProjectHub.cs` (implementation)
3. **TypeScript types** → `signalr/types.ts` + `signalr/hub.ts`
4. **React UI** → `components/{Feature}/` with CSS
5. **Store action** → `store/index.ts` (add to ActivePage union if it's a page)
6. **Shell routing** → `Shell.tsx` (render the component)
7. **Sidebar entry** → `Sidebar.tsx` (add menu item)

All UI lives in React. The MAUI app is a thin WebView host — never add native .NET UI.

### Key Patterns

**SignalR strongly-typed hubs** — every client↔server method is defined in shared interfaces. TypeScript mirrors the C# types with PascalCase properties.

**File-based configuration** — adding a profile = creating a directory. Adding an MCP server = writing a JSON file. No database, no shadow state. The file tree IS the configuration.

**Config-driven roots** — roots are declarative templates. The server reads them fresh on each operation (no caching, no restart needed). Config overlays merge base + action.

**MCP injection** — profile MCP servers are merged with root/action MCP servers and written to a temp config file passed via `--mcp-config` to Claude Code. OAuth tokens are injected as headers.

---

## Roots & Actions — The Power of Templates

Roots are the heart of GodMode. They define **what Claude does** when a project starts. A root is a directory with a `.godmode-root/` folder containing configuration, input schemas, and scripts.

### Anatomy of a Root

```
my-root/
└── .godmode-root/
    ├── config.json              # Base configuration
    ├── config.analyze.json      # "analyze" action overlay
    ├── config.implement.json    # "implement" action overlay
    ├── schema.json              # Default input form
    ├── analyze/
    │   └── schema.json          # Analyze action form
    ├── implement/
    │   ├── schema.json          # Implement action form
    │   └── create.sh            # Implement-specific setup
    └── scripts/
        ├── prepare.sh           # Runs before project creation
        ├── create.sh            # Runs during creation
        └── delete.sh            # Cleanup on deletion
```

### Example 1: Simple Chat Root

The simplest root — just a name and a prompt:

**config.json:**
```json
{
  "description": "Open-ended Claude chat",
  "nameTemplate": "{name}",
  "promptTemplate": "{prompt}"
}
```

**schema.json:**
```json
{
  "type": "object",
  "properties": {
    "name": { "type": "string", "title": "Project Name" },
    "prompt": { "type": "string", "title": "What should Claude do?", "x-multiline": true }
  },
  "required": ["name", "prompt"]
}
```

### Example 2: Jira Ticket Worker with Multiple Actions

A root with two actions — "analyze" for investigation and "implement" for coding:

**config.json** (base):
```json
{
  "description": "Work on Jira tickets with automatic context fetching",
  "nameTemplate": "jira-{ticketId}",
  "environment": {
    "JIRA_BASE_URL": "https://mycompany.atlassian.net"
  }
}
```

**config.analyze.json** (analyze action overlay):
```json
{
  "promptTemplate": "Analyze Jira ticket {ticketId}. Fetch the ticket details, read linked issues, and provide a technical analysis with effort estimate and implementation plan."
}
```

**config.implement.json** (implement action overlay):
```json
{
  "promptTemplate": "Implement Jira ticket {ticketId}. Fetch ticket details, create a feature branch, implement the solution, write tests, and create a PR. Update the ticket status when done.",
  "create": "implement/create.sh"
}
```

**analyze/schema.json:**
```json
{
  "type": "object",
  "properties": {
    "ticketId": { "type": "string", "title": "Ticket ID", "description": "e.g. PROJ-123" }
  },
  "required": ["ticketId"]
}
```

**implement/schema.json:**
```json
{
  "type": "object",
  "properties": {
    "ticketId": { "type": "string", "title": "Ticket ID" },
    "repoUrl": { "type": "string", "title": "Repository URL" },
    "baseBranch": { "type": "string", "title": "Base Branch", "default": "main" }
  },
  "required": ["ticketId", "repoUrl"]
}
```

When creating a project, the user picks between "analyze" and "implement" — each shows its own form and sends its own prompt to Claude.

### Example 3: Git Repository Setup with Scripts

A root that clones a repo and sets up a development environment:

**config.json:**
```json
{
  "description": "Clone and work on a Git repository",
  "nameTemplate": "{repoName}",
  "promptTemplate": "{task}",
  "scriptsCreateFolder": true,
  "prepare": "scripts/prepare.sh",
  "create": "scripts/create.sh",
  "delete": "scripts/delete.sh"
}
```

**scripts/create.sh:**
```bash
#!/bin/bash
set -e

cd "$GODMODE_PROJECT_PATH"

# Clone the repository
git clone "$GODMODE_INPUT_REPOURL" .
git checkout -b "godmode/$GODMODE_PROJECT_ID"

# Install dependencies if package.json exists
if [ -f "package.json" ]; then
  npm install
fi

echo "Repository cloned and ready"
```

**scripts/delete.sh:**
```bash
#!/bin/bash
set -e
# Push any uncommitted work before deletion
cd "$GODMODE_PROJECT_PATH"
if [ -d ".git" ]; then
  git add -A
  git commit -m "WIP: GodMode session ended" 2>/dev/null || true
  git push origin HEAD 2>/dev/null || true
fi
```

### Example 4: Scheduled Report Generator

A root designed for scheduled execution:

**config.json:**
```json
{
  "description": "Generate weekly status report from Jira and GitHub",
  "nameTemplate": "report-{date}",
  "promptTemplate": "Generate a weekly status report. Use Jira MCP to find completed tickets this week. Use GitHub MCP to list merged PRs. Summarize progress, blockers, and next week's priorities. Format as markdown."
}
```

**schema.json:**
```json
{
  "type": "object",
  "properties": {
    "name": { "type": "string", "title": "Report Name", "default": "weekly-report" },
    "sprintName": { "type": "string", "title": "Sprint", "description": "Jira sprint to report on" }
  },
  "required": ["name"]
}
```

This root works with schedules — set it to run every Friday at 5pm with "Reuse Project" disabled to create a new report each week.

### How Actions Work

Actions are config overlays that merge with the base:

1. Base `config.json` defines shared settings (environment, scripts, description)
2. `config.{action}.json` overrides specific fields (nameTemplate, promptTemplate, create script)
3. `{action}/schema.json` defines action-specific form fields
4. Everything not overridden inherits from base

**Merge rules:** Action values win on conflict. Arrays are replaced, not merged. Environment variables from both levels are combined.

**MCP server merge:** Profile MCP servers → Root MCP servers → Action MCP servers. Later levels override earlier ones on key conflict.

### Script Environment Variables

All scripts receive these environment variables:

| Variable | Description |
|---|---|
| `GODMODE_ROOT_PATH` | Path to the root directory |
| `GODMODE_PROJECT_PATH` | Path to the project directory |
| `GODMODE_PROJECT_ID` | Unique project identifier |
| `GODMODE_PROJECT_NAME` | Human-readable project name |
| `GODMODE_RESULT_FILE` | Path to write structured results |
| `GODMODE_INPUT_{FIELD}` | Each form field value (uppercased key) |

### Server Deployment Script Rules

Scripts run inside Docker containers on cloud platforms. Follow these rules:

- No `chmod` (Azure Files doesn't support it)
- No `sudo` (container runs as non-root)
- No `apt-get`/`yum`/`brew` (only Node.js/npm/npx available)
- No interactive commands (headless environment)
- Always start with `set -e`
- Use `mkdir -p` (idempotent, no brace expansion)
- Only `.sh` scripts needed for server deployments
