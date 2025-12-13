# GodMode.Maui - Claude Autonomous Development System Control Plane

This is a cross-platform .NET MAUI application serving as the control plane for managing Claude instances across local machines and GitHub Codespaces.

## Architecture

The application follows the MVVM pattern with clear separation of concerns:

```
GodMode.Maui/
├── Abstractions/          # Core interfaces
│   ├── IHostProvider.cs
│   └── IProjectConnection.cs
├── Providers/             # Host and connection implementations
│   ├── GitHubCodespaceProvider.cs
│   ├── LocalFolderProvider.cs
│   ├── SignalRProjectConnection.cs
│   └── LocalProjectConnection.cs
├── Services/              # Business logic layer
│   ├── ProfileService.cs
│   ├── HostConnectionService.cs
│   ├── ProjectService.cs
│   └── NotificationService.cs
├── ViewModels/            # MVVM ViewModels
│   ├── MainViewModel.cs
│   ├── HostViewModel.cs
│   └── ProjectViewModel.cs
├── Views/                 # XAML UI Views
│   ├── MainPage.xaml
│   ├── HostPage.xaml
│   └── ProjectPage.xaml
└── Converters/            # Value converters for XAML binding
    ├── IsNotNullConverter.cs
    ├── BoolToColorConverter.cs
    ├── StateToColorConverter.cs
    └── OutputTypeToColorConverter.cs
```

## Key Components

### 1. Host Abstraction Layer

**IHostProvider**: Interface for host environment providers
- Lists available hosts
- Starts/stops hosts
- Establishes connections

**IProjectConnection**: Interface for project management
- Lists projects on a host
- Creates and manages projects
- Sends input and receives output
- Subscribes to real-time updates

### 2. Implementations

**GitHubCodespaceProvider**: Manages GitHub Codespaces via Octokit
- Lists user's codespaces
- Starts/stops codespaces
- Connects via SignalR to remote server

**LocalFolderProvider**: Manages local file-based projects
- Direct file system access
- Optional local SignalR server connection

**SignalRProjectConnection**: Real-time connection to SignalR server
- Bi-directional communication
- Live output streaming
- Automatic reconnection

**LocalProjectConnection**: Direct file system operations
- File watchers for output monitoring
- Local project CRUD operations

### 3. Services Layer

**ProfileService**:
- Manages user profiles with account configurations
- Encrypts GitHub tokens
- Persists to local storage

**HostConnectionService**:
- Manages active connections to hosts
- Connection pooling and reuse
- Automatic retry logic with exponential backoff
- Connection health monitoring

**ProjectService**:
- High-level project operations
- Status caching with TTL
- Output subscription management

**NotificationService**:
- System notifications for project events
- Badge count tracking
- Event-driven notification system

### 4. ViewModels

**MainViewModel**:
- Profile selection
- Host listing
- Navigation to host details

**HostViewModel**:
- Host status display
- Project listing
- Host start/stop operations
- Navigation to project details

**ProjectViewModel**:
- Project status board
- Real-time output stream
- Input handling
- Metrics display

### 5. Views

**MainPage**: Profile selector and host list
**HostPage**: Host details and project list
**ProjectPage**: Split view with status board and chat interface

## Dependencies

- **Microsoft.Maui.Controls**: MAUI framework
- **Microsoft.AspNetCore.SignalR.Client**: Real-time communication
- **Octokit**: GitHub API client
- **CommunityToolkit.Mvvm**: MVVM helpers
- **System.Reactive**: Reactive Extensions for output streaming
- **GodMode.Shared**: Shared types and models

## Configuration

### Dependency Injection

All services, view models, and views are registered in `MauiProgram.cs`:
- Services as Singletons (stateful)
- ViewModels and Views as Transient (per-navigation)

### Routing

Shell navigation is configured in `AppShell.xaml.cs`:
- `main` -> MainPage
- `host` -> HostPage (with profileName, hostId parameters)
- `project` -> ProjectPage (with profileName, hostId, projectId parameters)

## Building

### Windows

The project is currently configured to target Windows only:

```bash
dotnet build
```

### Multi-Platform Build

To enable Android, iOS, and macOS targets, update `GodMode.Maui.csproj`:

```xml
<TargetFrameworks>net9.0-android;net9.0-ios;net9.0-maccatalyst;net9.0-windows10.0.19041.0</TargetFrameworks>
```

Note: Android requires Android SDK installation.

## Data Storage

- **Profiles**: Stored in `FileSystem.AppDataDirectory/profiles.json`
- **Encryption Key**: Stored in `FileSystem.AppDataDirectory/.encryption_key`
- **Local Projects**: Configurable per profile (default: user's documents folder)

## Security

- GitHub tokens are encrypted using AES encryption
- Encryption keys are stored securely in app data directory
- Codespace connections use GitHub authentication via port forwarding
- Local connections can use optional shared secrets

## Known Issues & Notes

1. **Namespace Compatibility**: The existing `GodMode.Shared` project uses different namespaces (`GodMode.Shared.Models`, `GodMode.Shared.Enums`) and some types have different signatures than originally specified in specs2.md. The implementations in this MAUI project may need adjustments to match the actual shared types.

2. **Android SDK**: Multi-platform builds require Android SDK installation. Currently configured for Windows-only development.

3. **GitHub Codespaces API**: Octokit doesn't have native Codespaces support. The implementation uses direct REST API calls through Octokit's connection object.

## Future Enhancements

- [ ] SSH host provider
- [ ] Azure/AWS VM providers
- [ ] Offline mode with request queuing
- [ ] Background service for Android
- [ ] Push notifications
- [ ] Cost tracking and budgets
- [ ] Project templates
- [ ] Team/shared projects

## Testing

Run tests with:

```bash
dotnet test
```

## Contributing

When adding new features:
1. Follow MVVM pattern
2. Use dependency injection
3. Add value converters for complex XAML bindings
4. Document public APIs
5. Handle errors gracefully with user feedback
