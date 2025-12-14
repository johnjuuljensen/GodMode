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

    private void SetupDefaultProjectRoots()
    {
        var roots = new[] { new ProjectRoot("default", "/projects") };
        ProjectService.ListProjectRootsAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(roots);
    }

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
        vm.IsLoading.Should().BeFalse();
        vm.ErrorMessage.Should().BeNull();
        vm.SelectedProjectType.Should().Be(ProjectType.RawFolder);
        vm.RequiresRepoUrl.Should().BeFalse();
    }

    [Fact]
    public void RequiresRepoUrl_WhenRawFolder_ShouldBeFalse()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.SelectedProjectType = ProjectType.RawFolder;

        // Assert
        vm.RequiresRepoUrl.Should().BeFalse();
    }

    [Fact]
    public void RequiresRepoUrl_WhenGitHubRepo_ShouldBeTrue()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.SelectedProjectType = ProjectType.GitHubRepo;

        // Assert
        vm.RequiresRepoUrl.Should().BeTrue();
    }

    [Fact]
    public void RequiresRepoUrl_WhenGitHubWorktree_ShouldBeTrue()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.SelectedProjectType = ProjectType.GitHubWorktree;

        // Assert
        vm.RequiresRepoUrl.Should().BeTrue();
    }

    #endregion

    #region LoadAsync Tests

    [Fact]
    public async Task LoadCommand_ShouldLoadProjectRoots()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";

        var roots = new[] {
            new ProjectRoot("default", "/projects"),
            new ProjectRoot("work", "/work/projects")
        };
        ProjectService.ListProjectRootsAsync("TestProfile", "host1")
            .Returns(roots);

        // Act
        await vm.LoadCommand.ExecuteAsync(null);

        // Assert
        vm.ProjectRoots.Should().HaveCount(2);
        vm.SelectedProjectRoot.Should().Be(roots[0]);
    }

    [Fact]
    public async Task LoadCommand_WhenError_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";

        ProjectService.ListProjectRootsAsync("TestProfile", "host1")
            .ThrowsAsync(new Exception("Connection failed"));

        // Act
        await vm.LoadCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Contain("Connection failed");
        vm.IsLoading.Should().BeFalse();
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
        vm.SelectedProjectRoot = new ProjectRoot("default", "/projects");

        // Act
        await vm.CreateCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Be("Please enter a project name");
        await ProjectService.DidNotReceive().CreateProjectAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<ProjectType>(), Arg.Any<string?>(), Arg.Any<string>());
    }

    [Fact]
    public async Task CreateCommand_WhenNoProjectRootSelected_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectName = "MyProject";
        vm.InitialPrompt = "Build a web app";
        vm.SelectedProjectRoot = null;

        // Act
        await vm.CreateCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Be("Please select a project root");
    }

    [Fact]
    public async Task CreateCommand_WhenGitHubRepoAndNoUrl_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectName = "MyProject";
        vm.SelectedProjectType = ProjectType.GitHubRepo;
        vm.RepoUrl = "";
        vm.InitialPrompt = "Build a web app";
        vm.SelectedProjectRoot = new ProjectRoot("default", "/projects");

        // Act
        await vm.CreateCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Be("Please enter a repository URL");
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
        vm.SelectedProjectRoot = new ProjectRoot("default", "/projects");

        // Act
        await vm.CreateCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Be("Please enter an initial prompt");
    }

    #endregion

    #region CreateAsync Success Tests

    [Fact]
    public async Task CreateCommand_WhenValidRawFolder_ShouldCreateProject()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectName = "MyProject";
        vm.SelectedProjectType = ProjectType.RawFolder;
        vm.InitialPrompt = "Build a web app";
        vm.SelectedProjectRoot = new ProjectRoot("default", "/projects");

        var status = new ProjectStatus(
            "MyProject", "MyProject", ProjectState.Running,
            DateTime.UtcNow, DateTime.UtcNow, null, null,
            new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0);
        var detail = new ProjectDetail(status, "session123");

        ProjectService.CreateProjectAsync("TestProfile", "host1", "MyProject",
            "default", ProjectType.RawFolder, null, "Build a web app")
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
            "TestProfile", "host1", "MyProject", "default", ProjectType.RawFolder, null, "Build a web app");
    }

    [Fact]
    public async Task CreateCommand_WhenValidGitHubRepo_ShouldCreateProject()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectName = "MyProject";
        vm.SelectedProjectType = ProjectType.GitHubRepo;
        vm.RepoUrl = "https://github.com/user/repo";
        vm.InitialPrompt = "Build a web app";
        vm.SelectedProjectRoot = new ProjectRoot("default", "/projects");

        var status = new ProjectStatus(
            "MyProject", "MyProject", ProjectState.Running,
            DateTime.UtcNow, DateTime.UtcNow, "https://github.com/user/repo", null,
            new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0);
        var detail = new ProjectDetail(status, "session123");

        ProjectService.CreateProjectAsync("TestProfile", "host1", "MyProject",
            "default", ProjectType.GitHubRepo, "https://github.com/user/repo", "Build a web app")
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
            "TestProfile", "host1", "MyProject", "default", ProjectType.GitHubRepo,
            "https://github.com/user/repo", "Build a web app");
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
        vm.SelectedProjectRoot = new ProjectRoot("default", "/projects");

        ProjectService.CreateProjectAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ProjectType>(),
            Arg.Any<string?>(), Arg.Any<string>())
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
        vm.SelectedProjectRoot = new ProjectRoot("default", "/projects");

        var creatingStates = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CreateProjectViewModel.IsCreating))
                creatingStates.Add(vm.IsCreating);
        };

        var status = new ProjectStatus(
            "MyProject", "MyProject", ProjectState.Running,
            DateTime.UtcNow, DateTime.UtcNow, null, null,
            new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0);
        var detail = new ProjectDetail(status, "session123");

        ProjectService.CreateProjectAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ProjectType>(),
            Arg.Any<string?>(), Arg.Any<string>())
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
}
