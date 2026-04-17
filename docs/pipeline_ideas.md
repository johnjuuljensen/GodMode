# Pipeline Ideas — Agent-to-Agent Task Passing

## The Problem

A single Claude agent is good at focused tasks. But real workflows span multiple concerns:

1. **Analyst** — reads a Jira ticket, understands it against the codebase, rewrites it as a precise spec
2. **Coder** — takes the spec and implements it
3. **Reviewer** — reviews the code, flags issues, requests changes

Each stage benefits from a different root config (different prompts, tools, MCP servers, constraints). Passing work between them today is manual: read output, copy-paste, create new project.

---

## Actions — What They Are

Actions are the entry points into a root. A root can have one or many actions, each defining a different way to create a project.

### Where actions come from

Actions are discovered from the `.godmode-root/` directory by convention:

```
my-root/
└── .godmode-root/
    ├── config.json              # Base config (shared across all actions)
    ├── config.analyze.json      # Action overlay: "analyze"
    ├── config.implement.json    # Action overlay: "implement"
    ├── config.review.json       # Action overlay: "review"
    ├── analyze/
    │   └── schema.json          # Input schema for "analyze" action
    ├── implement/
    │   └── schema.json          # Input schema for "implement" action
    └── review/
        └── schema.json          # Input schema for "review" action
```

- `config.json` is the base — shared environment, claudeArgs, MCP servers
- `config.{name}.json` is an action overlay — merged on top of base (overrides fields)
- `{name}/schema.json` defines the input form (JSON Schema → dynamic UI form)
- If no action overlays exist, the root has a single implicit action called "Create"

### What an action contains

```typescript
{
  name: string;              // "analyze", "implement", "review"
  description?: string;      // Shown in the UI action picker
  inputSchema?: JsonSchema;  // Defines the form fields (name, prompt, custom fields)
  prepare?: string[];        // Scripts to run before Claude starts
  create?: string[];         // Scripts to run to set up the project folder
  delete?: string[];         // Scripts to run on project deletion
  environment?: Record;      // Extra env vars for Claude
  claudeArgs?: string[];     // Extra CLI args for Claude
  nameTemplate?: string;     // Project name from inputs, e.g. "{ticketId}"
  promptTemplate?: string;   // Initial prompt from inputs, e.g. "Implement {task}"
  model?: string;            // Override model for this action
  mcpServers?: Record;       // Action-specific MCP servers (merged with profile + root)
  scriptsCreateFolder?: bool; // Whether the create script makes the folder itself
}
```

### Action merge order

MCP servers and environment vars merge in three levels:

```
Profile (broadest) → Root base (config.json) → Action overlay (config.{name}.json)
```

Action wins on conflict. This means a root can share common setup (env, MCP) across all actions while each action customizes its prompt, schema, and model.

### Types of actions and use cases

| Action Type | Purpose | Example |
|-------------|---------|---------|
| **Freeform** | Open-ended task | `config.json` with `{name}` + `{prompt}` — user types anything |
| **Template** | Structured task from inputs | Jira worker: `config.jira.json` with `{ticketId}` → fetches ticket via MCP |
| **Pipeline stage** | Receives output from previous stage | `config.implement.json` with `{spec}` input — filled by pipeline engine |
| **Specialized** | Different Claude configurations | `config.quick.json` uses haiku for fast tasks, `config.deep.json` uses opus |
| **Script-heavy** | Setup-intensive | `config.deploy.json` runs `prepare.sh` to clone repo, install deps, then Claude works |

#### Concrete examples

**Multi-action root: "fullstack-dev"**
```
fullstack-dev/.godmode-root/
├── config.json                    # Base: shared env, GitHub MCP, codebase context
├── config.feature.json           # New feature: prompt template focuses on implementation
├── config.bugfix.json            # Bug fix: prompt template focuses on reproduction + fix
├── config.refactor.json          # Refactor: prompt template focuses on safety + tests
├── feature/schema.json           # Inputs: name, description, acceptance criteria
├── bugfix/schema.json            # Inputs: ticket ID, reproduction steps
└── refactor/schema.json          # Inputs: target area, constraints
```

**Multi-action root: "jira-pipeline"**
```
jira-pipeline/.godmode-root/
├── config.json                    # Base: Jira MCP, GitHub MCP
├── config.analyze.json           # Stage 1: read ticket, output spec
├── config.implement.json         # Stage 2: code from spec
├── config.review.json            # Stage 3: review PR
├── analyze/schema.json           # Input: ticketId
├── implement/schema.json         # Input: spec (from stage 1)
└── review/schema.json            # Input: prUrl, spec (from stage 1)
```

---

## Option A: Pipeline as First-Class Concept

A pipeline definition lives alongside actions in a root:

```json
// .godmode-root/pipeline.json
{
  "name": "Jira-to-PR",
  "stages": [
    {
      "name": "analyze",
      "root": "jira-pipeline",
      "action": "analyze",
      "gate": "auto"
    },
    {
      "name": "implement",
      "root": "jira-pipeline",
      "action": "implement",
      "inputMapping": { "spec": "$.stages.analyze.result" },
      "gate": "manual"
    },
    {
      "name": "review",
      "root": "jira-pipeline",
      "action": "review",
      "inputMapping": {
        "prUrl": "$.stages.implement.result.prUrl",
        "spec": "$.stages.analyze.result"
      },
      "gate": "auto"
    }
  ]
}
```

### How it works

1. User triggers pipeline (UI button or webhook)
2. `PipelineEngine` creates project for stage 1
3. Monitors project until state = `Idle` (completed)
4. Extracts result (see "Result extraction" below)
5. If next stage gate is `auto`, immediately creates next project with mapped inputs
6. If gate is `manual`, shows approval UI — user reviews output, clicks "Approve & Continue"
7. Repeat until all stages complete

### UI for pipelines

```
Pipeline: JIRA-123 Flow
┌─────────────────────────────────────────────┐
│ ✅ Analyze          "Rewrite ticket as spec" │
│    12m ago · jira-pipeline/analyze           │
│    [View Output]                             │
├─────────────────────────────────────────────┤
│ ⏸  Implement        Awaiting approval        │
│    [View Spec] [Edit & Continue] [Approve]   │
├─────────────────────────────────────────────┤
│ ○  Review           Pending                  │
└─────────────────────────────────────────────┘
```

### Pros
- Declarative, reproducible, version-controlled
- Clear UI with stage progress and human gates
- Input mapping is explicit
- Easy to retry a single stage
- Can mix roots (analyze in one root, code in another)

### Cons
- New server-side concept (PipelineEngine, state tracking)
- Need result extraction convention
- More complex than ad-hoc approaches

---

## Option B: Webhook Chaining

Each project's completion fires a webhook that creates the next project.

```
[Analyst project completes]
    → POST /webhook/start-coder { body: analyst output }
        → [Coder project completes]
            → POST /webhook/start-reviewer { body: coder output }
```

### How it works

- Configure webhooks for each stage transition
- Claude (or a post-completion script) calls the webhook URL with results
- Webhook creates next project with input mapping from payload

### Pros
- Already built — webhooks exist today
- Loosely coupled — stages are fully independent
- Works with external systems (GitHub Actions, CI/CD, Slack bots)
- No new server concepts

### Cons
- **No pipeline visibility** — just independent projects, no stage tracking
- **Claude can't reliably call webhooks** — needs a tool or script
- **Output extraction is ad-hoc** — each webhook caller decides what to pass
- **Human gates are awkward** — need a separate approval pattern
- **Error handling is fragile** — chain breaks silently on failure
- **No retry** — can't re-run stage 2 with same inputs easily

---

## Option C: GodMode MCP Server — Claude Reports Back

**This is the most interesting idea.** Give Claude an MCP server that lets it interact with GodMode itself. Claude can submit results, create follow-up projects, check on other projects, and participate in pipelines.

### The GodMode MCP Server

An MCP server (stdio, bundled with the GodMode server) that exposes GodMode operations as tools. Added to projects via profile MCP config — Claude gets it automatically.

#### Core tools

| Tool | Description | Use Case |
|------|-------------|----------|
| `godmode_submit_result` | Write structured result for this project | Pipeline stage completion, result extraction |
| `godmode_create_project` | Create a new project in a root/action | Agent spawns follow-up work |
| `godmode_send_to_project` | Send input to another running project | Agent-to-agent communication |
| `godmode_get_project_status` | Check status of another project | Wait for dependency to complete |
| `godmode_get_project_output` | Read output from another project | Review what another agent produced |
| `godmode_list_projects` | List projects in a root/profile | Discover related work |
| `godmode_update_status` | Set a custom status message | "Analyzing ticket...", "Running tests..." |
| `godmode_request_human_review` | Pause and ask for human approval | Pipeline gate, uncertain decisions |
| `godmode_trigger_webhook` | Fire a webhook | Chain to external systems |
| `godmode_log` | Write to a structured activity log | Audit trail, debugging |

#### How `godmode_submit_result` enables pipelines

Instead of the pipeline engine parsing output.jsonl to guess what the "result" is, Claude explicitly submits it:

```
Claude (analyst): I've analyzed JIRA-123. Here's the refined spec.

[calls godmode_submit_result with {
  "summary": "Authentication bypass in /api/users endpoint",
  "spec": "The endpoint at /api/users/:id does not validate...",
  "prUrl": null,
  "confidence": "high"
}]
```

The pipeline engine reads the structured result and maps it to the next stage's inputs. No guessing, no parsing.

#### How `godmode_create_project` enables ad-hoc delegation

Claude discovers during analysis that a task is actually two independent sub-tasks:

```
Claude (analyst): This ticket has two parts — a backend API change and a frontend update.
They can be done in parallel.

[calls godmode_create_project with {
  "root": "coder",
  "action": "implement",
  "inputs": { "spec": "Backend: add validation to..." }
}]

[calls godmode_create_project with {
  "root": "coder",
  "action": "implement",
  "inputs": { "spec": "Frontend: update form to..." }
}]
```

This is **agent-initiated fan-out** — something a static pipeline definition can't express.

#### How `godmode_request_human_review` enables gates

```
Claude (coder): I've implemented the feature but I'm not confident about
the database migration approach. I'd like a human to review before I
create the PR.

[calls godmode_request_human_review with {
  "question": "Should I use a reversible migration or is destructive OK?",
  "context": "The migration drops a column that has 2M rows of data"
}]
```

This puts the project in `WaitingInput` state with a rich context. The human responds in the UI, and Claude continues.

#### Implementation sketch

The MCP server runs as a sidecar process, communicating with GodMode server via HTTP (localhost):

```
Claude ←stdio→ [godmode-mcp-server] ←HTTP→ [GodMode Server API]
```

Or simpler: the MCP server is a thin wrapper that calls the same SignalR hub methods or a lightweight REST API on the server. The server already has all the logic — the MCP server is just a bridge.

```typescript
// godmode-mcp-server/tools/submit_result.ts
server.tool("godmode_submit_result", async (params) => {
  await fetch(`http://localhost:31337/api/projects/${projectId}/result`, {
    method: "POST",
    body: JSON.stringify(params.result)
  });
  return { success: true };
});
```

The MCP server knows its own project ID (passed via env var `GODMODE_PROJECT_ID`).

#### Pros
- **Claude is an active participant**, not a black box that produces text
- Structured results — no parsing heuristics
- Agent-initiated workflows — Claude decides what happens next
- Human gates are natural (request_human_review)
- Fan-out and fan-in patterns become possible
- Status updates give real-time pipeline visibility
- The MCP server is useful beyond pipelines (any Claude project can use it)

#### Cons
- New MCP server to build and maintain
- Security: Claude can create projects, which could spiral (need rate limits / depth limits)
- Need a REST API or internal endpoint on the server (SignalR is client→server, not MCP→server)

### Additional MCP tools worth considering

| Tool | Description |
|------|-------------|
| `godmode_read_file` | Read a file from another project's working directory |
| `godmode_get_root_config` | Inspect available roots and their actions |
| `godmode_get_profile_info` | See what MCP servers and env vars are in the current profile |
| `godmode_search_projects` | Find projects by name, state, or root |
| `godmode_cancel_project` | Stop another project |
| `godmode_get_pipeline_status` | Check overall pipeline progress |
| `godmode_emit_metric` | Record structured metrics (tokens used, files changed, tests passed) |

---

## Recommended Approach: Pipeline + GodMode MCP

Combine Option A (declarative pipelines) with Option C (GodMode MCP server):

1. **Pipelines define the structure** — stages, gates, input mapping
2. **GodMode MCP lets Claude participate** — submit results, request reviews, update status
3. **Webhooks are the external interface** — trigger pipelines from GitHub, Slack, CI/CD

### The flow

```
                    ┌──────────────────────┐
  Webhook/UI ──────►│   Pipeline Engine    │
                    │   (server-side)      │
                    └──────┬───────────────┘
                           │ creates projects
                    ┌──────▼───────────────┐
                    │   Stage 1: Analyze   │
                    │   Claude + MCP tools │
                    │                      │
                    │   godmode_submit_result({...})
                    └──────┬───────────────┘
                           │ result extracted
                    ┌──────▼───────────────┐
                    │   Gate: manual        │
                    │   [Approve] in UI    │◄──── Human reviews
                    └──────┬───────────────┘
                           │ approved
                    ┌──────▼───────────────┐
                    │   Stage 2: Implement │
                    │   Claude + MCP tools │
                    │                      │
                    │   godmode_submit_result({prUrl: "..."})
                    └──────┬───────────────┘
                           │
                    ┌──────▼───────────────┐
                    │   Stage 3: Review    │
                    └──────────────────────┘
```

### Result extraction priority

1. **Explicit**: Claude calls `godmode_submit_result` → structured JSON, always preferred
2. **Convention**: `.godmode/result.json` file in project directory → fallback
3. **Heuristic**: Last assistant message from output.jsonl → last resort

### Implementation order

| Phase | What | Why first |
|-------|------|-----------|
| 1 | GodMode MCP server with `submit_result`, `update_status`, `request_human_review` | Useful immediately in any project, not just pipelines |
| 2 | REST API on server for MCP→server communication | Required for MCP server to work |
| 3 | Pipeline engine with stage sequencing and gates | Builds on submit_result for result extraction |
| 4 | Pipeline UI (progress, gate approval) | Visibility into running pipelines |
| 5 | Advanced MCP tools (`create_project`, `get_project_output`) | Agent autonomy, fan-out patterns |
| 6 | Webhook triggers for pipelines | External integration |

---

## Open Questions

1. **Pipeline scope**: Should pipelines be cross-root (stages in different roots) or single-root (stages as actions within one root)? Cross-root is more flexible but harder to manage.

2. **Result schema**: Should each action define an output schema (like it defines an input schema)? This would enable validation and typed input mapping between stages.

3. **Parallel stages**: Should pipelines support fan-out (stage 1 → stages 2a + 2b → stage 3)? The GodMode MCP approach handles this naturally (Claude decides to create multiple projects), but declarative pipelines would need explicit syntax.

4. **Pipeline persistence**: Where does pipeline state live? Options: in-memory (lost on restart), in a `pipeline-runs/` directory (file-based, consistent with the rest of GodMode), or in the database (if we ever add one).

5. **MCP server auth**: How does the MCP server authenticate to the GodMode server? Options: shared secret via env var, project-scoped token, or localhost-only with no auth.

6. **Depth limits**: If Claude can create projects that create projects, how do we prevent infinite recursion? Suggestion: `GODMODE_PIPELINE_DEPTH` env var, max depth of 3-5.
