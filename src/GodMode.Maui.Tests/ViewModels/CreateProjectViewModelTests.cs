using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using GodMode.Maui.ViewModels;
using GodMode.Shared.Models;
using GodMode.Shared.Enums;

namespace GodMode.Maui.Tests.ViewModels;

public class CreateProjectViewModelTests : TestBase
{
    private CreateProjectViewModel CreateViewModel() =>
        new(ProjectService);

    #region Initial State Tests

    [Fact]
    public void Constructor_ShouldInitializeWithDefaultValues()
    {
        // Act
        var vm = CreateViewModel();

        // Assert
        vm.ProfileName.Should().BeEmpty();
        vm.HostId.Should().BeEmpty();
        vm.ProjectName.Should().BeEmpty();
        vm.RepoUrl.Should().BeNull();
        vm.InitialPrompt.Should().BeEmpty();
        vm.IsCreating.Should().BeFalse();
        vm.ErrorMessage.Should().BeNull();
    }

    #endregion

    #region CreateAsync Validation Tests

    [Fact]
    public async Task CreateCommand_WhenProjectNameEmpty_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectName = "";
        vm.InitialPrompt = "Build a web app";

        // Act
        await vm.CreateCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Be("Please enter a project name");
        await ProjectService.DidNotReceive().CreateProjectAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string>());
    }

    [Fact]
    public async Task CreateCommand_WhenProjectNameWhitespace_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectName = "   ";
        vm.InitialPrompt = "Build a web app";

        // Act
        await vm.CreateCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Be("Please enter a project name");
    }

    [Fact]
    public async Task CreateCommand_WhenInitialPromptEmpty_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectName = "MyProject";
        vm.InitialPrompt = "";

        // Act
        await vm.CreateCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Be("Please enter an initial prompt");
    }

    [Fact]
    public async Task CreateCommand_WhenInitialPromptWhitespace_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectName = "MyProject";
        vm.InitialPrompt = "   ";

        // Act
        await vm.CreateCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Be("Please enter an initial prompt");
    }

    #endregion

    #region CreateAsync Success Tests

    [Fact]
    public async Task CreateCommand_WhenValid_ShouldCreateProject()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectName = "MyProject";
        vm.RepoUrl = "https://github.com/user/repo";
        vm.InitialPrompt = "Build a web app";

        var status = new ProjectStatus(
            "proj1", "MyProject", ProjectState.Running,
            DateTime.UtcNow, DateTime.UtcNow, "https://github.com/user/repo", null,
            new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0);
        var detail = new ProjectDetail(status, "session123");

        ProjectService.CreateProjectAsync("TestProfile", "host1", "MyProject",
            "https://github.com/user/repo", "Build a web app")
            .Returns(detail);

        // Act - Shell.Current will throw, but we can verify CreateProjectAsync was called
        try
        {
            await vm.CreateCommand.ExecuteAsync(null);
        }
        catch (NullReferenceException)
        {
            // Expected - Shell.Current is null in tests
        }

        // Assert
        await ProjectService.Received(1).CreateProjectAsync(
            "TestProfile", "host1", "MyProject", "https://github.com/user/repo", "Build a web app");
    }

    [Fact]
    public async Task CreateCommand_WhenRepoUrlEmpty_ShouldPassNullRepoUrl()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectName = "MyProject";
        vm.RepoUrl = ""; // Empty repo URL
        vm.InitialPrompt = "Build a web app";

        var status = new ProjectStatus(
            "proj1", "MyProject", ProjectState.Running,
            DateTime.UtcNow, DateTime.UtcNow, null, null,
            new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0);
        var detail = new ProjectDetail(status, "session123");

        ProjectService.CreateProjectAsync("TestProfile", "host1", "MyProject", null, "Build a web app")
            .Returns(detail);

        // Act
        try
        {
            await vm.CreateCommand.ExecuteAsync(null);
        }
        catch (NullReferenceException)
        {
            // Expected - Shell.Current is null in tests
        }

        // Assert
        await ProjectService.Received(1).CreateProjectAsync(
            "TestProfile", "host1", "MyProject", null, "Build a web app");
    }

    [Fact]
    public async Task CreateCommand_WhenRepoUrlWhitespace_ShouldPassNullRepoUrl()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectName = "MyProject";
        vm.RepoUrl = "   "; // Whitespace repo URL
        vm.InitialPrompt = "Build a web app";

        var status = new ProjectStatus(
            "proj1", "MyProject", ProjectState.Running,
            DateTime.UtcNow, DateTime.UtcNow, null, null,
            new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0);
        var detail = new ProjectDetail(status, "session123");

        ProjectService.CreateProjectAsync("TestProfile", "host1", "MyProject", null, "Build a web app")
            .Returns(detail);

        // Act
        try
        {
            await vm.CreateCommand.ExecuteAsync(null);
        }
        catch (NullReferenceException)
        {
            // Expected - Shell.Current is null in tests
        }

        // Assert
        await ProjectService.Received(1).CreateProjectAsync(
            "TestProfile", "host1", "MyProject", null, "Build a web app");
    }

    #endregion

    #region CreateAsync Error Handling Tests

    [Fact]
    public async Task CreateCommand_WhenExceptionThrown_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectName = "MyProject";
        vm.InitialPrompt = "Build a web app";

        ProjectService.CreateProjectAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>())
            .ThrowsAsync(new Exception("Creation failed"));

        // Act
        await vm.CreateCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Contain("Creation failed");
        vm.IsCreating.Should().BeFalse();
    }

    #endregion

    #region IsCreating State Tests

    [Fact]
    public async Task CreateCommand_ShouldSetIsCreatingDuringExecution()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectName = "MyProject";
        vm.InitialPrompt = "Build a web app";

        var creatingStates = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CreateProjectViewModel.IsCreating))
                creatingStates.Add(vm.IsCreating);
        };

        var status = new ProjectStatus(
            "proj1", "MyProject", ProjectState.Running,
            DateTime.UtcNow, DateTime.UtcNow, null, null,
            new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0);
        var detail = new ProjectDetail(status, "session123");

        ProjectService.CreateProjectAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>())
            .Returns(async _ =>
            {
                await Task.Delay(10);
                return detail;
            });

        // Act
        try
        {
            await vm.CreateCommand.ExecuteAsync(null);
        }
        catch (NullReferenceException)
        {
            // Expected - Shell.Current is null in tests
        }

        // Assert - Should have been true then false
        creatingStates.Should().Contain(true);
        creatingStates.Last().Should().BeFalse();
    }

    #endregion

    #region ErrorMessage Reset Tests

    [Fact]
    public async Task CreateCommand_ShouldClearErrorMessageOnNewAttempt()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectName = "MyProject";
        vm.InitialPrompt = "Build a web app";
        vm.ErrorMessage = "Previous error"; // Set a previous error

        var status = new ProjectStatus(
            "proj1", "MyProject", ProjectState.Running,
            DateTime.UtcNow, DateTime.UtcNow, null, null,
            new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0);
        var detail = new ProjectDetail(status, "session123");

        ProjectService.CreateProjectAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>())
            .Returns(detail);

        string? errorMessageDuringExecution = "not cleared";
        ProjectService.CreateProjectAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>())
            .Returns(_ =>
            {
                errorMessageDuringExecution = vm.ErrorMessage;
                return detail;
            });

        // Act
        try
        {
            await vm.CreateCommand.ExecuteAsync(null);
        }
        catch (NullReferenceException)
        {
            // Expected - Shell.Current is null in tests
        }

        // Assert - ErrorMessage should be cleared at the start of execution
        errorMessageDuringExecution.Should().BeNull();
    }

    #endregion
}
