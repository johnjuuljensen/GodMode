# Investigation: Actions on Roots

## Current State

Actions are **project-creation entry points**. A root with multiple actions (e.g., `config.analyze.json`, `config.implement.json`) offers different ways to create a project, each with its own prompt template, input schema, scripts, and model.

### How actions work today

```
.godmode-root/
├── config.json                 # Base config (shared)
├── config.analyze.json         # Action: "analyze" (overlay)
├── config.implement.json       # Action: "implement" (overlay)
├── analyze/schema.json         # Input form for "analyze"
└── implement/schema.json       # Input form for "implement"
```

- `RootConfigReader` discovers actions by scanning for `config.*.json` files
- If none exist, the root has a single implicit action called "Create"
- Action overlays merge on top of base config (scalars override, dicts merge, arrays concat)
- `CreateProject(profile, root, actionName, inputs)` is the only way to invoke an action
- UI shows actions in the "Create Project" dialog as a dropdown/picker

### Limitations

1. **Actions = project creation only** — `CreateAction` model has `NameTemplate`, `PromptTemplate`, `ScriptsCreateFolder` — all project-creation concepts
2. **No standalone execution** — can't "run an action" on an existing project or without creating a project
3. **No action results** — actions don't produce structured output. The pipeline doc proposes `godmode_submit_result` to fix this.
4. **No action chaining** — no way for one action's output to feed another action's input (pipeline territory)

## Findings

Actions are already well-designed for their purpose. The "investigation" reveals that the gap isn't in the action system itself but in what sits **on top** of actions:

### What actions should NOT become
- Actions should not become a generic "run arbitrary scripts" system
- Actions should not become a separate concept from project creation — they ARE the way projects are created

### What's actually needed

| Need | Solution | Where it lives |
|------|----------|---------------|
| Different ways to start work in a root | **Actions** (already exists) | `.godmode-root/config.*.json` |
| Passing work between stages | **Pipelines** (Task 1) | `.godmode-root/pipeline.json` |
| Running work on a schedule | **Schedules** (Task 2) | `.profiles/{name}/schedules/*.json` |
| Claude reporting results back | **GodMode MCP** (Task 1, Phase 1) | MCP server with `submit_result` |
| Running something on an existing project | **Resume with new input** | Already supported via `ResumeProject` |

### Recommendation

**Do not extend the action system.** Instead, implement pipelines (Task 1) which give actions their missing capabilities:

1. **Structured output** — `godmode_submit_result` MCP tool lets Claude report results from any action
2. **Chaining** — `pipeline.json` connects action outputs to next-action inputs
3. **Gates** — Pipeline gates add human review between stages
4. **Standalone re-runs** — Pipeline UI lets you re-run a stage (which creates a new project from the same action with adjusted inputs)

The action system is the **building block**. Pipelines are the **orchestrator**. Schedules are the **trigger**. Keep them separate.

### One enhancement worth making

When implementing pipelines, add an optional `outputSchema` to `CreateAction`:

```json
// config.analyze.json
{
  "description": "Analyze a Jira ticket and produce a spec",
  "nameTemplate": "analyze-{ticketId}",
  "promptTemplate": "Analyze ticket {ticketId} and produce a spec...",
  "outputSchema": {
    "type": "object",
    "properties": {
      "spec": { "type": "string", "description": "The implementation spec" },
      "complexity": { "type": "string", "enum": ["low", "medium", "high"] }
    }
  }
}
```

This enables:
- Pipeline engine validates stage output against schema before proceeding
- UI can show expected output structure
- Input mapping between stages becomes typed: `"spec": "${stages.analyze.result.spec}"`

### File changes needed (deferred to Task 1)

- `CreateAction.cs`: Add optional `OutputSchema` property
- `RootConfigReader.cs`: Parse `outputSchema` from action config
- `CreateActionInfo` (shared): Include output schema for UI display
- Pipeline engine: validate `godmode_submit_result` payload against output schema

## Conclusion

No standalone work needed on "Actions on Roots." The current action system is sound. The gaps (output, chaining, scheduling) are addressed by Tasks 1 (Pipeline) and 2 (Schedules). The only action-level enhancement (`outputSchema`) should be done as part of Pipeline implementation.
