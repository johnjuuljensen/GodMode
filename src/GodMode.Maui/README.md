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

The MAUI app has a companion test project (`GodMode.Maui.Tests`) with comprehensive ViewModel unit tests.

### Running Tests

```bash
# Run all tests
dotnet test src/GodMode.Maui.Tests/GodMode.Maui.Tests.csproj

# Run with verbose output
dotnet test src/GodMode.Maui.Tests/GodMode.Maui.Tests.csproj --logger "console;verbosity=detailed"

# Run specific test class
dotnet test src/GodMode.Maui.Tests/GodMode.Maui.Tests.csproj --filter "FullyQualifiedName~MainViewModelTests"
```

### Test Structure

```
GodMode.Maui.Tests/
├── GlobalUsings.cs           # Global usings (xUnit, MAUI stubs)
├── MauiCompatibility.cs      # MAUI type stubs for test environment
├── TestBase.cs               # Base class with mocked services
└── ViewModels/
    ├── MainViewModelTests.cs         # 10 tests
    ├── HostViewModelTests.cs         # 13 tests
    ├── ProjectViewModelTests.cs      # 17 tests
    ├── AddProfileViewModelTests.cs   # 15 tests
    └── CreateProjectViewModelTests.cs # 16 tests
```

### Testing Architecture

The test project uses:
- **xUnit**: Test framework
- **NSubstitute**: Mocking framework
- **FluentAssertions**: Assertion library
- **Linked Source Files**: ViewModels are compiled directly in the test project

Key approach:
1. **Interface-based DI**: Services implement interfaces (`IProfileService`, `IHostConnectionService`, etc.) for easy mocking
2. **MAUI Stubs**: `MauiCompatibility.cs` provides stub implementations of MAUI types (`Shell`, `Application`, `MainThread`, etc.)
3. **TestBase**: Base class provides pre-configured mocks for all services

### Adding Tests for New ViewModels

When adding a new ViewModel to the MAUI app, follow these steps:

#### 1. Ensure the ViewModel uses Interface Dependencies

```csharp
// Good - testable
public partial class MyNewViewModel : ObservableObject
{
    private readonly IMyService _myService;

    public MyNewViewModel(IMyService myService)
    {
        _myService = myService;
    }
}

// Bad - not testable
public partial class MyNewViewModel : ObservableObject
{
    private readonly MyService _myService = new();
}
```

#### 2. Create the Test Class

Create a new file `ViewModels/MyNewViewModelTests.cs`:

```csharp
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using GodMode.Maui.ViewModels;
using GodMode.Shared.Models;

namespace GodMode.Maui.Tests.ViewModels;

public class MyNewViewModelTests : TestBase
{
    private MyNewViewModel CreateViewModel() =>
        new(ProfileService, /* other services */);

    #region Initial State Tests

    [Fact]
    public void Constructor_ShouldInitializeWithDefaultValues()
    {
        // Act
        var vm = CreateViewModel();

        // Assert
        vm.SomeProperty.Should().BeNull();
        vm.IsLoading.Should().BeFalse();
    }

    #endregion

    #region Command Tests

    [Fact]
    public async Task SomeCommand_WhenValid_ShouldPerformAction()
    {
        // Arrange
        var vm = CreateViewModel();
        ProfileService.SomeMethodAsync().Returns(Task.FromResult("result"));

        // Act
        await vm.SomeCommand.ExecuteAsync(null);

        // Assert
        await ProfileService.Received(1).SomeMethodAsync();
        vm.SomeProperty.Should().Be("result");
    }

    [Fact]
    public async Task SomeCommand_WhenExceptionThrown_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        ProfileService.SomeMethodAsync().ThrowsAsync(new Exception("Test error"));

        // Act
        await vm.SomeCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Contain("Test error");
        vm.IsLoading.Should().BeFalse();
    }

    #endregion
}
```

#### 3. Add New Service Interfaces to TestBase (if needed)

If your ViewModel needs a new service, add its mock to `TestBase.cs`:

```csharp
public abstract class TestBase
{
    protected IProfileService ProfileService { get; }
    protected IMyNewService MyNewService { get; }  // Add new service
    // ...

    protected TestBase()
    {
        ProfileService = Substitute.For<IProfileService>();
        MyNewService = Substitute.For<IMyNewService>();  // Initialize mock
        // ...
    }
}
```

#### 4. Handle Shell Navigation in Tests

Shell navigation will throw `NullReferenceException` in tests since `Shell.Current` is null. Wrap navigation-triggering code in try-catch and verify behavior before the exception:

```csharp
[Fact]
public async Task NavigateCommand_ShouldNavigateToPage()
{
    // Arrange
    var vm = CreateViewModel();

    // Act & Assert - Shell.Current is null in tests
    try
    {
        await vm.NavigateCommand.ExecuteAsync(null);
    }
    catch (NullReferenceException)
    {
        // Expected - Shell.Current is null in tests
    }

    // Assert the work that happens before navigation
    SomeService.Received(1).SomeMethod();
}
```

### Test Categories

Organize tests into regions:

- **Initial State Tests**: Verify constructor sets defaults correctly
- **Command Tests**: Test `[RelayCommand]` async/sync methods
- **Property Change Tests**: Verify `PropertyChanged` events fire correctly
- **Error Handling Tests**: Verify exceptions are caught and `ErrorMessage` is set
- **Validation Tests**: Test input validation logic
- **Loading State Tests**: Verify `IsLoading` is true during async operations

### Tips

1. **Use `Task.FromResult` for async mocks**: NSubstitute's `Returns()` needs explicit `Task.FromResult<T>()` for async methods
2. **Use `ThrowsAsync` for exceptions**: Import `NSubstitute.ExceptionExtensions` for `ThrowsAsync`
3. **Test PropertyChanged events**: Subscribe to `PropertyChanged` before the action and collect values in a list
4. **Avoid testing MAUI UI directly**: The test project cannot reference MAUI assemblies - test ViewModels only

## Contributing

When adding new features:
1. Follow MVVM pattern
2. Use dependency injection with interfaces
3. Add value converters for complex XAML bindings
4. Document public APIs
5. Handle errors gracefully with user feedback
6. **Add unit tests for all new ViewModels** (see Testing section above)
