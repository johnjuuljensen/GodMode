# GodMode.Server

SignalR server for the Claude Autonomous Development System. This lightweight .NET server manages Claude Code processes and provides real-time communication with clients.

## Features

- **Real-time Communication**: SignalR hub for bidirectional communication
- **Process Management**: Spawn and control Claude Code processes using CliWrap
- **File Watching**: Monitor output.jsonl files for real-time updates
- **State Persistence**: Save and recover project state across restarts
- **Git Integration**: Track git status and changes
- **Metrics Tracking**: Monitor tokens, cost, and performance

## Architecture

### Components

1. **ProjectHub** - SignalR hub exposing client-server API
2. **ProjectManager** - Orchestrates project lifecycle and file management
3. **ClaudeProcessManager** - Manages Claude Code processes via CliWrap
4. **StatusUpdater** - Updates status.json based on events and git status

### Project Structure

Each project is stored in a folder:

```
/projects/{project-id}/
├── status.json          # Current project state
├── input.jsonl          # User input log
├── output.jsonl         # Claude output log
├── session-id           # Claude session ID for resumption
└── work/                # Working directory for Claude
```

## Configuration

Configuration is in `appsettings.json`:

```json
{
  "ProjectsPath": "projects",    // Root folder for projects
  "Urls": "http://0.0.0.0:31337"  // Server URL
}
```

Environment variable override: `PROJECTS_PATH`

## Running the Server

### Development

```bash
dotnet run
```

### Production

```bash
dotnet publish -c Release -o publish
cd publish
./GodMode.Server
```

### Docker

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY publish/ .
ENTRYPOINT ["dotnet", "GodMode.Server.dll"]
```

## SignalR Hub API

### Server → Client Events

- `OutputReceived(projectId, outputEvent)` - New output from Claude
- `StatusChanged(projectId, status)` - Project status changed
- `ProjectCreated(project)` - New project created
- `ProjectRemoved(projectId)` - Project deleted

### Client → Server Methods

- `Task<ProjectSummary[]> ListProjects()` - Get all projects
- `Task<ProjectStatus> GetStatus(projectId)` - Get project status
- `Task<ProjectDetail> CreateProject(name, repoUrl?, initialPrompt)` - Create new project
- `Task SendInput(projectId, input)` - Send input to Claude
- `Task StopProject(projectId)` - Stop running project
- `Task SubscribeProject(projectId, outputOffset)` - Subscribe to output events
- `Task UnsubscribeProject(projectId)` - Unsubscribe from output
- `Task<string> GetMetricsHtml(projectId)` - Get metrics dashboard HTML

## Dependencies

- **.NET 9.0** - Runtime
- **SignalR** - Real-time communication
- **CliWrap** - Process management
- **GodMode.Shared** - Shared types and models

## Development

### Building

```bash
dotnet build
```

### Testing

```bash
dotnet test
```

### Debugging

Set `ASPNETCORE_ENVIRONMENT=Development` for detailed logging.

## Deployment

### GitHub Codespaces

Add to `.devcontainer/devcontainer.json`:

```json
{
  "postStartCommand": "cd /workspaces/GodMode/src/GodMode.Server && dotnet run &",
  "forwardPorts": [31337],
  "portsAttributes": {
    "31337": {
      "label": "GodMode Server",
      "visibility": "private"
    }
  }
}
```

### Local Machine

Run as a system service:

**Windows** (using NSSM):
```
nssm install GodModeServer "C:\path\to\GodMode.Server.exe"
nssm start GodModeServer
```

**Linux** (systemd):
```ini
[Unit]
Description=GodMode Server

[Service]
WorkingDirectory=/opt/godmode
ExecStart=/usr/bin/dotnet /opt/godmode/GodMode.Server.dll
Restart=always

[Install]
WantedBy=multi-user.target
```

## Monitoring

Health check endpoint: `GET /health`

Returns:
```json
{
  "status": "healthy"
}
```

## Security

- **Codespaces**: Uses GitHub authentication via port forwarding
- **Local**: Configure CORS in `appsettings.json` or use reverse proxy with auth
- **Production**: Use HTTPS and proper authentication middleware

## Troubleshooting

### Claude Process Not Starting

- Ensure `claude` command is in PATH
- Check Claude CLI is installed: `claude --version`
- Review logs for CliWrap errors

### Projects Not Recovered on Startup

- Check `ProjectsPath` configuration
- Verify `status.json` files are valid JSON
- Review startup logs

### SignalR Connection Failures

- Verify CORS settings allow client origin
- Check firewall rules for port 31337
- Enable detailed SignalR logging

## License

MIT License
