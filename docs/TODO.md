# GodMode TODO

Work items for autonomous execution. Each item has enough context to be completed without user input.

---

## 1. Implement Pipeline (Full: MCP Server + Pipeline Engine + UI)

**Reference**: `docs/pipeline_ideas.md` — recommended approach (Option A + C combined)

**Scope**: All 6 phases in one implementation:

### Phase 1-2: GodMode MCP Server + REST API
- Create a new MCP server (stdio) that Claude processes get via `--mcp-config`
- Tools: `godmode_submit_result`, `godmode_update_status`, `godmode_request_human_review`
- MCP server communicates with GodMode.Server via a local REST API (localhost-only, project-scoped token auth)
- REST endpoints on GodMode.Server: `POST /api/internal/result`, `POST /api/internal/status`, `POST /api/internal/review`
- The MCP server binary/script lives in the repo; path injected via env var `GODMODE_MCP_PATH`

### Phase 3: Pipeline Engine
- `pipeline.json` in `.godmode-root/` defines stages, gates, and input mapping between stages
- Pipeline engine in `GodMode.Server/Services/PipelineEngine.cs`
- Stage sequencing: on project completion, check if it's part of a pipeline, extract result, evaluate gate, start next stage
- Result extraction priority: (1) `godmode_submit_result` call, (2) `.godmode/result.json` file, (3) last assistant message
- Gate types: `auto` (proceed immediately), `manual` (wait for human approval in UI)
- Input mapping: next stage inputs can reference previous stage results via `${stages.analyze.result.fieldName}`
- Pipeline state persisted to `{ProjectRootsDir}/pipeline-runs/{run-id}/state.json`

### Phase 4: Pipeline UI
- New `ActivePage` type: `pipelineView`
- Show pipeline progress: stages as steps, current stage highlighted, gate approval buttons
- Link each stage to its project for full output viewing
- Pipeline list view showing all active/completed pipeline runs

### Phase 5: Advanced MCP Tools
- `godmode_create_project` — Claude can spawn sub-projects (fan-out)
- `godmode_get_project_output` — read another project's results
- Depth limit via `GODMODE_PIPELINE_DEPTH` env var (default 3)

### Phase 6: Webhook Triggers for Pipelines
- Extend existing webhook system to support `"type": "pipeline"` targeting a pipeline instead of a single action

### Architecture constraints
- Follow `docs/UNIFIED-ARCHITECTURE.md` Section 5: declarative config, no shadow state
- `pipeline.json` is the source of truth for pipeline definition
- Pipeline run state is file-based (`pipeline-runs/` directory)

---

## 2. Implement Schedules

**Architecture**: File-based, per-profile, following `docs/UNIFIED-ARCHITECTURE.md` Section 5-6.

### File layout
```
{ProjectRootsDir}/
└── .profiles/
    └── {profileName}/
        └── schedules/
            └── {schedule-name}.json
```

### Schedule file format (`{schedule-name}.json`)
```json
{
  "description": "Daily standup analysis",
  "enabled": true,
  "cron": "0 9 * * 1-5",
  "target": {
    "type": "project",
    "rootName": "jira-worker",
    "actionName": "analyze",
    "inputs": {
      "query": "sprint active",
      "name": "standup-{date}"
    }
  }
}
```

Alternative target type for pipelines:
```json
{
  "target": {
    "type": "pipeline",
    "rootName": "feature-root",
    "pipelineName": "full-review"
  }
}
```

### Server-side
- `ScheduleManager` service in `GodMode.Server/Services/ScheduleManager.cs`
- On startup: scan `.profiles/*/schedules/*.json`, register cron timers for enabled schedules
- On trigger: create project (or start pipeline) with specified inputs
- Watch for file changes (schedules added/removed/modified) — reload without restart
- `{date}`, `{time}`, `{datetime}` placeholders in inputs resolved at trigger time
- Adding a schedule = write a JSON file. Deleting = delete the file. Enable/disable = edit `enabled` field.

### SignalR hub methods
- `GetSchedules(profileName)` — list schedules for a profile
- `CreateSchedule(profileName, name, config)` — write schedule file
- `UpdateSchedule(profileName, name, config)` — overwrite schedule file
- `DeleteSchedule(profileName, name)` — delete schedule file
- `ToggleSchedule(profileName, name, enabled)` — update enabled field
- Client callback: `ScheduleTriggered(profileName, scheduleName, projectId)` — notify UI when schedule fires

### React UI
- New sidebar menu item: "Schedules" (same level as Connectors, Roots, Profiles, Webhooks)
- New `ActivePage` type: `scheduleSettings`
- Schedule list view: name, cron (human-readable display), next run time, enabled toggle, edit/delete
- Schedule create/edit form: human-friendly UI (dropdowns for common patterns: "Every day at", "Every hour", "Every N minutes", "Weekdays at") that generates cron under the hood. Show raw cron for advanced users.
- Target selector: choose profile root + action (with input fields from schema) OR pipeline

### Key constraints
- Per-profile: schedules live in `.profiles/{name}/schedules/`. A schedule targets roots within its own profile.
- File-based: CRUD = file operations. No database, no in-memory-only state.
- Cron internally, human-friendly UI on top
- Manifest round-trip: export reads schedule files, apply writes them

---

## 3. GodMode Chat Improvements

**Goal**: Chat needs domain knowledge about GodMode features so it can guide users.

### System prompt
- Rewrite GodMode Chat system prompt with comprehensive knowledge of:
  - What roots are, how to create and configure them (config.json, actions, scripts, schema.json)
  - What profiles are and how MCP servers/env vars attach to them
  - What pipelines are and how to set them up (after pipeline is implemented)
  - What schedules are and how to configure them (after schedules are implemented)
  - How OAuth connectors work
  - How webhooks work

### RAG examples
- Include concrete examples in the system prompt or as retrievable context:
  - Example root config with multiple actions
  - Example pipeline.json
  - Example schedule.json
  - Example profile setup with MCP servers
  - Common troubleshooting (MCP not connecting, permission errors, script not found)

### Update cadence
- System prompt should be updated whenever new features land (pipeline, schedules, etc.)

---

## 4. Fix Google MCPs

**Problem**: Anthropic-hosted connectors (`gmail.mcp.claude.ai`, `gcal.mcp.claude.ai`, `drive.mcp.claude.ai`) are internal to Claude's infrastructure. They cannot be reached via `--mcp-config` with custom OAuth tokens.

### Options (pick one during implementation)
1. **Remove from catalog** — Remove Gmail, Google Calendar, Google Drive from `connectors-catalog.ts`. Add a note that these require Claude.ai account setup.
2. **Replace with community alternatives** — Find self-hosted MCP servers for Google services that accept OAuth tokens as env vars or headers. Update catalog entries with new URLs/commands.
3. **Hybrid** — Keep catalog entries but mark them as "Requires Claude.ai" with different UI treatment (no OAuth flow, just instructions).

### Related cleanup
- Remove the `"type": "sse"` → `"http"` transport inference logic if no Anthropic-hosted connectors remain
- Clean up debug logging in `OAuthProxyClient.RedeemRelayTokenAsync`
- Remove or simplify `FetchGoogleUserInfoAsync` if proxy now returns email directly

---

## 5. Screen Takeover Bug

**Problem**: Cannot open GodMode Chat when certain pages (mcpConfig, rootManager, etc.) are open.

**Root cause**: In `Shell.tsx`, `activePage` takes priority over `showGodModeChat` in the render logic (lines 146-154 desktop, lines 113-115 mobile). When the user clicks the GodMode Chat button, `showGodModeChat` is set to true, but `activePage` is not cleared — so the page keeps rendering.

**Fix**: In the store action that toggles `showGodModeChat`, also clear `activePage`:
```typescript
toggleGodModeChat: () => set(s => ({
  showGodModeChat: !s.showGodModeChat,
  activePage: !s.showGodModeChat ? null : s.activePage  // clear page when opening chat
}))
```

Also ensure the reverse: when `setActivePage` is called, clear `showGodModeChat`.

---

## 6. OAuth Proxy Work Order

**For**: Cloudflare Worker proxy developer

### Issue 1: Reduce Google login scopes
- Current: proxy requests broad Google scopes on login flow
- Required: login only needs `openid email profile` — nothing else
- The `scope` query parameter is now passed from the GodMode instance to the proxy's `/authorize` endpoint
- Proxy should use the `scope` param when provided, only fall back to defaults for connector flows

### Issue 2: Fix consent screen branding
- Current: Google consent screen shows "worker.dev is asking for permission"
- Fix: Configure OAuth consent screen in Google Cloud Console with proper app name and branding
- The OAuth client ID used by the proxy needs its consent screen configured with:
  - App name: "GodMode" (or similar)
  - Authorized domains: the production domain
  - Logo and privacy/TOS URLs if required for verification

---

## 7. Login Screen Documentation

**Goal**: Create a doc describing the login screen design so it can be reused in other projects.

### Contents
- Screenshot or description of the login page layout
- Auth flow: challenge endpoint → redirect to OAuth proxy → relay callback → cookie session
- React components involved: `App.tsx` (auth check), `LoginPage.tsx` (redirect button), `Auth.css`
- Server endpoints: `/api/auth/challenge`, `/api/oauth/initiate`, `/api/oauth/relay`, `/api/auth/logout`
- Auth modes: google, codespace, apikey, none
- How to adapt for other projects (what to change, what stays the same)

---

## 8. Test All MCPs

**Goal**: Verify each connector in `connectors-catalog.ts` works end-to-end.

### Manual test plan
For each connector:
1. Add via UI (or OAuth flow for OAuth connectors)
2. Create a project on a profile with the connector
3. Ask Claude to use the connector's tools
4. Verify tool calls succeed

### Automation investigation
- Can we write a test that starts a server, adds a connector, creates a project, and verifies MCP tools appear in the init message?
- The init message `mcp_servers` field shows `connected`/`failed` status — assert on this
- Would need test credentials or mock MCP servers

---

## 9. Investigate Actions on Roots

**Question**: What should root-level actions look like beyond the current create/prepare/delete lifecycle?

### Areas to explore
- Can a root define standalone actions (not tied to project creation)?
- Example: "Run linter", "Deploy", "Generate report" as actions on an existing project
- How do actions interact with pipelines? (A pipeline stage = an action on a root)
- Should actions be discoverable via the UI sidebar per-root?
- Review `docs/pipeline_ideas.md` "Actions" section (lines 15-107) for the existing design
