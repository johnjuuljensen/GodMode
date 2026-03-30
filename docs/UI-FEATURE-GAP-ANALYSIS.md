# UI Feature Gap Analysis: RE-ARCHITECTURE-PLAN vs JUNIOR

Comparing JUNIOR branch React UI features against the 14-PR plan to identify gaps.

---

## Covered by the Plan

| Feature | PR | Notes |
|---------|-----|-------|
| MCP config panel (manual add/remove) | PR 9 | Replaces McpBrowser+McpProfilePanel. No Smithery search. |
| MCP types (McpServerConfig etc.) | PR 7 | |
| Root Manager (create/edit/delete) | PR 10 | No template tab (templates are external). File editor instead. |
| Root import from git/GitHub | PR 10 | Replaces JUNIOR's URL/file import |
| Root export (.gmroot ZIP) | PR 10 | |
| Root types (RootPreview, RootManifest etc.) | PR 8 | |
| Profile Settings (edit description) | PR 10 | |
| Create Profile modal | PR 10 | |
| Profile hub wrappers (createProfile, updateProfileDescription) | PR 7 | |
| MCP servers shown in CreateProject flow | PR 9 | |
| Shell header buttons (Root Manager, Profile Settings, Create Profile) | PR 9, PR 10 | |

---

## NOT Covered — Needs Adding

### 1. ProjectView Status Button Redesign
JUNIOR replaces three separate buttons (Stop, Resume, Delete) with a unified status button:
- Colored dot with animated pulse (green=running, blue=waiting, gray=stopped, red=error)
- Hover reveals action (Stop/Resume)
- Delete becomes icon-only (trash SVG)
- Much cleaner look

**Recommendation:** Small standalone PR or fold into an existing UI PR.

### 2. MCP Badges in ProjectView Header
JUNIOR shows effective MCP servers as small badges in the project header:
- Up to 3 server names shown inline
- 4+ shows a count badge with hover tooltip
- Monospace, glass styling

**Recommendation:** Add to PR 9 (React MCP UI) since it depends on `getEffectiveMcpServers`.

### 3. Sidebar Profile Filtering & Grouping
JUNIOR's Sidebar groups projects by profile and respects `profileFilter`:
- Projects with empty ProfileName show under all profiles
- Root groups within profile sections
- Profile section headers with project counts

Master already has profile groups and filtering but JUNIOR refined the empty-profile logic.

**Recommendation:** Small fix PR or fold into PR 10.

### 4. CreateProject Root Picker Redesign
JUNIOR's CreateProject has a two-step flow:
- **Step 1:** Grid of root picker cards (name, description, action list, profile badge)
- **Step 2:** Project form with action selector, model selector, MCP panel

Master's CreateProject is simpler. The plan mentions "show MCP servers in create flow" (PR 9) but doesn't cover the root picker grid redesign or model selector.

**Recommendation:** Add root picker grid + model selector to PR 10 or a separate PR.

### 5. Feature Visibility Toggles (AppSettings)
JUNIOR has an AppSettings modal with toggles for:
- `roots` — show/hide Root Manager button
- `mcp` — show/hide MCP browser, badges, panels
- `profiles` — show/hide Profile settings, filter, create button

Plan explicitly marks this as "Out of scope."

**Recommendation:** Decide if this is wanted. If yes, tiny standalone PR (one component + store flags).

---

## Summary: Missing PRs / Additions

| Gap | Effort | Suggested Location |
|-----|--------|--------------------|
| ProjectView status button redesign | Small | New PR or add to PR 9/10 |
| MCP badges in ProjectView | Small | Add to PR 9 |
| Sidebar profile filtering refinement | Small | Add to PR 10 |
| CreateProject root picker grid + model selector | Medium | Add to PR 10 |
| Feature visibility toggles (AppSettings) | Small | New PR (optional) |
