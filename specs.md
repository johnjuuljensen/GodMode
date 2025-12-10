Claude Autonomous Development System - Architecture Plan
Components Overview
┌─────────────────────┐       ┌─────────────────────┐       ┌─────────────────────┐
│   GitHub Pages      │──────►│   MCP Server        │◄─────►│   Codespace(s)      │
│   Static SPA        │       │   AWS Lambda        │       │   Controller Daemon │
└─────────────────────┘       └─────────────────────┘       └─────────────────────┘
                                       │
                                       ▼
                              ┌─────────────────────┐
                              │   Storage           │
                              │   (S3 or Gists)     │
                              └─────────────────────┘

Component 1: MCP Server (AWS Lambda)
Purpose: Central relay and control plane, exposed as MCP server over SSE transport.
Responsibilities:

- GitHub OAuth token exchange
- Codespace lifecycle management (list, start, stop via GitHub API)
- Message relay between SPA and codespace controllers
- Conversation log storage
- Session state persistence (which projects exist, which codespace they're on)

MCP Tools to expose:

- codespaces_list - list user's codespaces with status
- codespace_start / codespace_stop - lifecycle control
- project_create - create new project, assign to codespace, start Claude
- project_list - list all projects across all codespaces
- project_send_input - relay user input to specific project
- project_get_status - get current status and pending question if any
- project_stream_subscribe - for SSE streaming of Claude output

MCP Resources to expose:

- project://{id}/conversation - conversation history
- project://{id}/status - current status

State storage (DynamoDB or S3):

- Projects: id, name, codespace assignment, session ID, status, current question
- User sessions: GitHub token (encrypted), preferences

Why Lambda + MCP:

- Serverless, scales to zero
- MCP is a standard protocol - the SPA can use any MCP client library
- SSE transport works well for streaming Claude output through the relay
- Single endpoint handles auth, control, and messaging


Component 2: Codespace Controller Daemon
Purpose: Local agent in each codespace that manages Claude processes.
Responsibilities:

- Connect to MCP server on startup (register as available)
- Receive commands: start project, send input, stop project
- Spawn and manage Claude Code processes with --output-format stream-json
- Stream Claude output back to MCP server
- Detect input requests, notify MCP server
- Persist local state to survive codespace restart
- Resume interrupted Claude sessions on restart

Communication with MCP server:

- WebSocket or long-poll connection to Lambda
- Heartbeat to indicate codespace is alive
- Push output events as they occur
- Receive commands (start, input, stop)

Local state (filesystem, persists across restarts):

- projects.json - list of projects assigned to this codespace
- Project work directories under /workspaces/projects/{id}/
- Claude sessions in ~/.claude/ (automatic)


Component 4: Dev Container Configuration
Purpose: Ensure controller daemon starts automatically in codespaces.
Configuration:

- Base image with .NET 8 (for controller) and Node (for Claude CLI)
- postStartCommand to launch controller daemon
- Codespace secret for MCP server connection credentials
- Forward port for any dev servers projects might run


Data Flow Examples
Creating a new project:

SPA calls MCP tool project_create with name, repo URL, initial prompt
MCP server picks a running codespace (or starts one)
MCP server sends command to that codespace's controller
Controller clones repo, starts Claude with initial prompt
Controller streams output to MCP server
MCP server relays to SPA via SSE
MCP server persists conversation to storage

User sends input:

SPA calls MCP tool project_send_input with project ID and input text
MCP server looks up which codespace owns that project
MCP server relays input to controller
Controller writes to Claude's stdin
Claude continues, output streams back through the chain

Codespace restarts:

Controller daemon starts via postStartCommand
Reads projects.json, finds projects that were running
Reconnects to MCP server, reports available
For each interrupted project, resumes Claude with --resume --session-id
MCP server updates project status, SPA sees it's back online


Infrastructure Summary
ComponentTechnologyCost ModelSPAGitHub PagesFreeMCP ServerAWS Lambda + API GatewayPay per requestStateDynamoDB or S3Minimal at low scaleLogsS3 or GitHub GistsFree/minimalCodespacesGitHub CodespacesPer-hour compute

Implementation Order

MCP Server skeleton - Lambda with basic auth and one test tool
Controller daemon - Can start Claude, stream output locally
MCP Server ↔ Controller connection - WebSocket relay working
SPA basics - OAuth flow, connect to MCP, list projects
Full project lifecycle - Create, stream, input, stop
Persistence and resume - Survive restarts, conversation logs
Polish - Multi-codespace support, error handling, UI refinement

