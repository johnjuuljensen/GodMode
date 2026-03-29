# Provisioning, Dynamic Configuration, and the Round-Trip Problem

## The tension

**Provisioned systems** (Docker images, Dockerfiles, IaC) are declarative and reproducible. You define the desired state upfront, build an artifact, and deploy it. The artifact is immutable — what you tested is what runs.

**Dynamic configuration** (runtime root creation, MCP server management, profile overrides) is the opposite. Users modify the system while it's running. This is essential for a tool like GodMode where the whole point is letting users set up and iterate on workspaces.

These two models conflict:

- A provisioned root baked into a Docker image can't be modified at runtime without diverging from the manifest.
- A root created dynamically at runtime doesn't exist in any manifest — it's invisible to provisioning, can't be reproduced, and is lost if the container is replaced.
- Config drift accumulates: the running system increasingly differs from what the Dockerfile describes.

## What this means for GodMode's architecture

### The shadow config problem (PR #78)

The `ProfileOverrideStore` approach — a runtime `~/.godmode/profile-overrides.json` layered on top of `appsettings.json` — creates a parallel config system. Two sources of truth, unclear precedence on redeployment, and the override file silently survives while the base config it was designed to overlay may have changed. This is config drift by design.

Similarly, putting Smithery registry search on the server makes the server responsible for discovering and mutating config at runtime. That's the wrong layer — discovery is a UI concern; the result should be written into declarative config, not injected through a runtime CRUD API.

### What belongs in the server (PR #78, the good parts)

- `McpServers` field on `ProfileConfig` and `CreateAction` — declarative, lives in existing config files
- `MergeMcpServers` in `RootConfigReader` — follows the existing base+overlay pattern (profile → root → action)
- Writing `mcp-config.json` and passing `--mcp-config` in `BuildClaudeConfig` — the injection mechanism into Claude instances

These are all statically resolvable at project creation time from config files on disk. No runtime state, no shadow stores.

### The template problem (PR #79)

12 built-in templates baked into the server's `Templates/` directory, with services to instantiate, package, and share them. Templates are useful, but they don't belong in the server binary — they're a deployment-time concern. Different teams want different templates. Bundling them couples the server release cycle to template authoring.

## The real goal: declarative convergence with round-trip support

The manifest is the complete desired state. If something is in the manifest, it should exist. If it's not, it should be removed. This is the Terraform model — not patches or diffs, but full-state convergence.

### The manifest

A manifest lives in a git repo. It declares the full configuration of a GodMode instance — roots, profiles, MCP servers, settings. It can reference roots from multiple sources, including sparse checkouts of subdirectories from other repos:

```yaml
roots:
  feature:
    git: https://github.com/acme/roots.git
    path: feature
    ref: v1.2
  bugfix:
    git: https://github.com/acme/roots.git
    path: bugfix
  custom:
    path: ./roots/custom  # same repo as the manifest
profiles:
  default:
    roots: [feature, bugfix, custom]
    mcpServers:
      github:
        command: npx
        args: ["-y", "@modelcontextprotocol/server-github"]
```

Removing a root or MCP server from the manifest and reapplying removes it from the running system. No explicit delete operations — absence is deletion.

### The three-phase loop

**1. Provision (apply manifest)**

On startup or redeployment, the server reads the manifest, compares it to what's on disk, and converges: creates what's missing, removes what's no longer declared, updates what's changed. Roots are fetched via sparse checkout if needed. This replaces both the Dockerfile `COPY` approach and runtime CRUD APIs.

Safety check: refuse to remove a root that has active projects (or require explicit confirmation).

**2. Modify at runtime**

Users can still make changes while the system is running. These changes write to the same config files on disk that the manifest would produce. There is no separate override store — the config files are the single source of truth.

**3. Export back to manifest**

The system can export its current state as a manifest. Since runtime changes wrote to the same config files, export is straightforward — read the current state, emit a manifest that would reproduce it. Commit the updated manifest to the repo, and the loop closes:

```
manifest → apply/converge → run → modify → export → commit manifest
```

### Composition without merging

Manifests compose by cherry-picking, not forking or merging. A team's manifest can reference roots from a shared roots repo (via git sparse checkout), from other teams' repos, or from local paths. Multiple manifests can coexist in the same repo for different deployment configurations.

The manifest format itself should support this naturally — each root entry is independent and points to its source. There's no inheritance hierarchy to manage.

## Tooling assessment

We evaluated existing declarative convergence tools for this:

**Terraform/OpenTofu** — Right convergence model, wrong domain. Designed for infrastructure resources (VMs, networks, DNS). Would require a custom GodMode provider. The investment doesn't match the config surface size.

**Ansible** — Push-based and procedural at heart. Can be made declarative per module, but doesn't naturally think in desired-state convergence.

**DSCv3** — Cross-platform now (Rust-based), declarative convergence with custom resources. Conceptually close, but the ecosystem is immature and the audience is small outside Windows.

**Helm/Kustomize** — Right composition patterns (base + overlay, strategic merge), but Kubernetes-specific.

**CUE** — Configuration language with built-in constraints, defaults, and deep merge. Solves the manifest composition problem (assembling config from multiple sources) cleanly. Not a convergence engine, but a strong fit for the manifest format and validation layer.

**Jsonnet** — Similar to CUE for templating/composition, but without the constraint system.

### Recommendation

**Git + a simple manifest format + built-in convergence.** The config surface is small — roots (directories), profiles (JSON), MCP servers (JSON fields). The convergence logic is a straightforward diff-and-reconcile loop, not worth the weight of Terraform or Ansible.

Where existing tools add genuine value:

- **Git** as the state store and versioning layer (GitOps)
- **CUE** (or similar) for manifest composition and validation, if the cherry-pick-from-multiple-sources pattern becomes complex enough to warrant it
- **Docker** for packaging the base server image
- **Git sparse checkout** for fetching root subdirectories from remote repos

The convergence engine itself is custom — a small reconciler in the server that reads the manifest, diffs against disk, and applies changes. This is tens of lines of code, not an infrastructure project.

## Design implications

- **No shadow config stores.** Runtime changes write to the same config files that provisioning populates. One source of truth.
- **No registry proxying on the server.** MCP discovery (Smithery, etc.) is a client/UI concern. The server consumes config; it doesn't help author it.
- **Templates are external.** They're roots like any other, living in git repos. The server doesn't bundle or manage them.
- **Export is a first-class operation.** The server can serialize its current config state back into manifest format.
- **The manifest is the complete desired state.** Convergence is additive and subtractive — what's declared exists, what's not declared gets removed.
- **The server's config file layout is the contract.** It serves as both the provisioning input format and the runtime state format. Same shape, same files, no translation layer.
