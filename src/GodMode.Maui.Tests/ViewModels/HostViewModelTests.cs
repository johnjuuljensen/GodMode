using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using GodMode.Maui.ViewModels;
using GodMode.ClientBase.Abstractions;
using GodMode.Shared.Models;
using GodMode.Shared.Enums;

namespace GodMode.Maui.Tests.ViewModels;

public class HostViewModelTests : TestBase
{
    private HostViewModel CreateViewModel() =>
        new(HostConnectionService, ProjectService, NotificationService);

    #region Initial State Tests

    [Fact]
    public void Constructor_ShouldInitializeWithDefaultValues()
    {
        // Act
        var vm = CreateViewModel();

        // Assert
        vm.ProfileName.Should().BeEmpty();
        vm.HostId.Should().BeEmpty();
        vm.HostStatus.Should().BeNull();
        vm.Projects.Should().BeEmpty();
        vm.IsLoading.Should().BeFalse();
        vm.ErrorMessage.Should().BeNull();
        vm.IsConnected.Should().BeFalse();
    }

    #endregion

    #region LoadAsync Tests

    [Fact]
    public async Task LoadCommand_WhenNoProfileName_ShouldDoNothing()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "";
        vm.HostId = "host1";

        // Act
        await vm.LoadCommand.ExecuteAsync(null);

        // Assert
        await HostConnectionService.DidNotReceive().GetProvidersForProfileAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task LoadCommand_WhenNoHostId_ShouldDoNothing()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "";

        // Act
        await vm.LoadCommand.ExecuteAsync(null);

        // Assert
        await HostConnectionService.DidNotReceive().GetProvidersForProfileAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task LoadCommand_WhenValidInput_ShouldLoadHostStatusAndProjects()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";

        var hostStatus = new HostStatus("host1", "Host 1", "local", HostState.Running, "/path", 2);
        var projects = new List<ProjectSummary>
        {
            new("proj1", "Project 1", ProjectState.Running, DateTime.UtcNow, null),
            new("proj2", "Project 2", ProjectState.Idle, DateTime.UtcNow, null)
        };

        var mockProvider = Substitute.For<IHostProvider>();
        mockProvider.Type.Returns("local");
        mockProvider.ListHostsAsync().Returns(Task.FromResult<IEnumerable<HostInfo>>(new List<HostInfo>
        {
            new("host1", "Host 1", "local", HostState.Running)
        }));
        mockProvider.GetHostStatusAsync("host1").Returns(Task.FromResult(hostStatus));

        HostConnectionService.IsConnected("TestProfile", "host1").Returns(false);
        HostConnectionService.GetProvidersForProfileAsync("TestProfile")
            .Returns(Task.FromResult<IEnumerable<IHostProvider>>(new List<IHostProvider> { mockProvider }));
        ProjectService.ListProjectsAsync("TestProfile", "host1").Returns(Task.FromResult<IEnumerable<ProjectSummary>>(projects));

        // Act
        await vm.LoadCommand.ExecuteAsync(null);

        // Assert
        vm.HostStatus.Should().NotBeNull();
        vm.HostStatus!.Name.Should().Be("Host 1");
        vm.Projects.Should().HaveCount(2);
        vm.IsConnected.Should().BeTrue();
        vm.IsLoading.Should().BeFalse();
        vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task LoadCommand_WhenExceptionThrown_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";

        HostConnectionService.IsConnected("TestProfile", "host1").Returns(false);
        HostConnectionService.GetProvidersForProfileAsync("TestProfile")
            .ThrowsAsync(new Exception("Connection failed"));

        // Act
        await vm.LoadCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Contain("Connection failed");
        vm.IsConnected.Should().BeFalse();
        vm.IsLoading.Should().BeFalse();
    }

    #endregion

    #region RefreshAsync Tests

    [Fact]
    public async Task RefreshCommand_ShouldReloadData()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";

        var mockProvider = Substitute.For<IHostProvider>();
        mockProvider.Type.Returns("local");
        mockProvider.ListHostsAsync().Returns(Task.FromResult<IEnumerable<HostInfo>>(new List<HostInfo>
        {
            new("host1", "Host 1", "local", HostState.Running)
        }));
        mockProvider.GetHostStatusAsync("host1").Returns(Task.FromResult(
            new HostStatus("host1", "Host 1", "local", HostState.Running, "/path", 0)));

        HostConnectionService.IsConnected("TestProfile", "host1").Returns(true);
        HostConnectionService.GetProvidersForProfileAsync("TestProfile")
            .Returns(Task.FromResult<IEnumerable<IHostProvider>>(new List<IHostProvider> { mockProvider }));
        ProjectService.ListProjectsAsync("TestProfile", "host1")
            .Returns(Task.FromResult<IEnumerable<ProjectSummary>>(new List<ProjectSummary>()));

        // Act
        await vm.RefreshCommand.ExecuteAsync(null);

        // Assert
        await HostConnectionService.Received().GetProvidersForProfileAsync("TestProfile");
    }

    #endregion

    #region StartHostAsync Tests

    [Fact]
    public async Task StartHostCommand_WhenNoHostStatus_ShouldDoNothing()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.HostStatus = null;

        // Act
        await vm.StartHostCommand.ExecuteAsync(null);

        // Assert
        await HostConnectionService.DidNotReceive().GetProvidersForProfileAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task StartHostCommand_ShouldStartHostAndRefresh()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.HostStatus = new HostStatus("host1", "Host 1", "local", HostState.Stopped, "/path", 0);

        var mockProvider = Substitute.For<IHostProvider>();
        mockProvider.Type.Returns("local");
        mockProvider.ListHostsAsync().Returns(Task.FromResult<IEnumerable<HostInfo>>(new List<HostInfo>
        {
            new("host1", "Host 1", "local", HostState.Running)
        }));
        mockProvider.GetHostStatusAsync("host1").Returns(Task.FromResult(
            new HostStatus("host1", "Host 1", "local", HostState.Running, "/path", 0)));

        HostConnectionService.GetProvidersForProfileAsync("TestProfile")
            .Returns(Task.FromResult<IEnumerable<IHostProvider>>(new List<IHostProvider> { mockProvider }));

        // Act
        await vm.StartHostCommand.ExecuteAsync(null);

        // Assert
        await mockProvider.Received(1).StartHostAsync("host1");
    }

    #endregion

    #region StopHostAsync Tests

    [Fact]
    public async Task StopHostCommand_WhenNoHostStatus_ShouldDoNothing()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.HostStatus = null;

        // Act
        await vm.StopHostCommand.ExecuteAsync(null);

        // Assert
        await HostConnectionService.DidNotReceive().GetProvidersForProfileAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task StopHostCommand_ShouldStopHostAndRefresh()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.HostStatus = new HostStatus("host1", "Host 1", "local", HostState.Running, "/path", 0);

        var mockProvider = Substitute.For<IHostProvider>();
        mockProvider.Type.Returns("local");
        mockProvider.ListHostsAsync().Returns(Task.FromResult<IEnumerable<HostInfo>>(new List<HostInfo>
        {
            new("host1", "Host 1", "local", HostState.Stopped)
        }));
        mockProvider.GetHostStatusAsync("host1").Returns(Task.FromResult(
            new HostStatus("host1", "Host 1", "local", HostState.Stopped, "/path", 0)));

        HostConnectionService.GetProvidersForProfileAsync("TestProfile")
            .Returns(Task.FromResult<IEnumerable<IHostProvider>>(new List<IHostProvider> { mockProvider }));

        // Act
        await vm.StopHostCommand.ExecuteAsync(null);

        // Assert
        await mockProvider.Received(1).StopHostAsync("host1");
    }

    #endregion

    #region NavigateToProjectAsync Tests

    [Fact]
    public async Task NavigateToProjectCommand_ShouldClearBadgeForProject()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";

        var project = new ProjectSummary("proj1", "Project 1", ProjectState.Running, DateTime.UtcNow, null);

        // Act - Note: Shell.Current will be null in tests, so we expect this to throw
        // We're testing the badge clear logic happens before navigation
        try
        {
            await vm.NavigateToProjectCommand.ExecuteAsync(project);
        }
        catch (NullReferenceException)
        {
            // Expected - Shell.Current is null in tests
        }

        // Assert
        NotificationService.Received(1).ClearBadgeCountForProject("TestProfile", "host1", "proj1");
    }

    [Fact]
    public async Task NavigateToProjectCommand_WithNullProject_ShouldDoNothing()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        await vm.NavigateToProjectCommand.ExecuteAsync(null!);

        // Assert
        NotificationService.DidNotReceive().ClearBadgeCountForProject(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    #endregion

    #region GetBadgeCount Tests

    [Fact]
    public void GetBadgeCount_ShouldReturnBadgeCountFromService()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";

        NotificationService.GetBadgeCount("TestProfile", "host1").Returns(3);

        // Act
        var count = vm.GetBadgeCount();

        // Assert
        count.Should().Be(3);
    }

    #endregion
}
