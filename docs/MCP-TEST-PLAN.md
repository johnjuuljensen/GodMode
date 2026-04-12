# MCP Connector Test Plan

## Connectors in Catalog

| # | Connector | Transport | Auth Type | Credentials Needed |
|---|-----------|-----------|-----------|-------------------|
| 1 | GitHub | stdio | Env var | `GITHUB_PERSONAL_ACCESS_TOKEN` |
| 2 | Jira | SSE + OAuth | OAuth (Atlassian) | Atlassian OAuth via proxy |
| 3 | Grafana | stdio | Env var | `GRAFANA_URL`, `GRAFANA_API_KEY` |
| 4 | Azure DevOps | stdio | Env var | `AZURE_DEVOPS_ORG_URL`, `AZURE_DEVOPS_AUTH_METHOD`, `AZURE_DEVOPS_PAT` |
| 5 | Vanta | SSE | Header (OAuth) | OAuth Client ID + Secret |
| 6 | Local Files | stdio | None | `ALLOWED_PATHS` (local path) |
| 7 | PowerPoint | SSE + OAuth | OAuth (Microsoft) | Microsoft OAuth via proxy |
| 8 | Fireflies | SSE | Header | API key (Bearer token) |
| 9 | Slack | stdio | Env var | `SLACK_BOT_TOKEN`, `SLACK_TEAM_ID` |

## Manual Test Procedure

For each connector:

### Step 1: Add connector
1. Open Connectors page in GodMode UI
2. Click the connector in the catalog
3. Fill in credentials
4. Verify it appears in the "Installed" list

### Step 2: Create project with connector
1. Create a project on a profile that has the connector
2. Check server logs for `MCP config JSON` — verify the connector is included
3. Check Claude init output for `mcp_servers` — verify status is `connected`

### Step 3: Use the connector
1. Ask Claude to use a tool from the connector
2. Verify the tool call succeeds (no permission errors, no connection errors)
3. Verify meaningful data is returned

### Per-Connector Tests

**1. GitHub** (`npx @modelcontextprotocol/server-github`)
- Add with a GitHub PAT
- Ask: "List my GitHub repos" or "Search GitHub issues in repo X"
- Expect: `mcp__github__*` tools available, repo data returned

**2. Jira** (`https://mcp.atlassian.com/v1/sse`)
- Connect via OAuth (Atlassian) in Connectors UI
- Ask: "List my Jira projects" or "Get issue PROJ-123"
- Expect: `mcp__jira__*` tools available, issue data returned
- Note: Requires Atlassian OAuth credentials configured in proxy

**3. Grafana** (`mcp-grafana` binary)
- Add with Grafana URL and API key
- Ask: "List Grafana datasources" or "Query Prometheus"
- Expect: `mcp__grafana__*` tools available

**4. Azure DevOps** (`npx @nicholasphair/azure-devops-mcp`)
- Add with org URL and PAT
- Ask: "List Azure DevOps projects"
- Expect: `mcp__azure__*` tools available

**5. Vanta** (`https://mcp.vanta.com/sse`)
- Add with OAuth client ID and secret
- Ask: "List Vanta integrations"
- Expect: `mcp__vanta__*` tools available

**6. Local Files** (`npx @anthropic/mcp-filesystem`)
- Add with an allowed path (e.g., `/tmp/test`)
- Ask: "List files in the allowed directory"
- Expect: `mcp__local-files__*` tools available, file listing returned

**7. PowerPoint** (`https://powerpoint.mcp.claude.ai/mcp`)
- **BLOCKED**: Uses Anthropic-internal endpoint. Cannot test via GodMode.
- Consider removing from catalog (same issue as Gmail/Calendar/Drive)

**8. Fireflies** (`https://mcp.fireflies.ai/sse`)
- Add with Fireflies API key
- Ask: "List my recent Fireflies meetings"
- Expect: `mcp__fireflies__*` tools available

**9. Slack** (`npx @anthropic/mcp-slack`)
- Add with Slack bot token and team ID
- Ask: "List Slack channels"
- Expect: `mcp__slack__*` tools available

## Automation Approach

### What can be automated

A smoke test that verifies MCP servers **connect** (not that they return useful data — that requires real credentials):

```typescript
// Test: start Claude with --mcp-config, verify init message shows "connected"
// This tests the plumbing, not the credentials

test('MCP server connects', async () => {
  const mcpConfig = { mcpServers: { 'local-files': { command: 'npx', args: [...], env: { ALLOWED_PATHS: '/tmp' } } } };
  // Write config, start claude --print --mcp-config config.json -p "list tools"
  // Parse init output, assert mcp_servers[0].status === 'connected'
});
```

### Realistic automation scope

| Level | What | Requires |
|-------|------|----------|
| **L1: Config generation** | Verify `BuildMcpConfigJson` produces correct JSON for each connector type | Unit test, no credentials |
| **L2: Connection smoke** | Verify Claude starts with MCP server and status=connected | `npx` available, no real credentials (local-files only) |
| **L3: Tool invocation** | Verify a tool call succeeds end-to-end | Real credentials, real services |

**Recommendation**: Implement L1 as unit tests (test `BuildMcpConfigJson` with mock data). L2 for local-files only (no credentials needed). L3 remains manual.

## Known Issues

- **PowerPoint**: Uses `powerpoint.mcp.claude.ai` — same internal-endpoint issue as Google connectors. Should be removed from catalog.
- **Jira**: Atlassian MCP server (`mcp.atlassian.com`) intermittently returns "We are having trouble completing this action" — server-side issue, not GodMode.
- **Vanta**: Untested — requires Vanta OAuth credentials.
