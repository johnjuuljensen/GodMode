using System.Text.Json;
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

    private static ProjectRootInfo CreateDefaultRoot(string name = "default", JsonElement? schema = null) =>
        new(name, "Test root", schema);

    private void SetupDefaultProjectRoots()
    {
        var roots = new[] { CreateDefaultRoot() };
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
        vm.IsCreating.Should().BeFalse();
        vm.IsLoading.Should().BeFalse();
        vm.ErrorMessage.Should().BeNull();
        vm.FormFields.Should().BeEmpty();
    }

    #endregion

    #region Dynamic Form Tests

    [Fact]
    public void SelectingProjectRoot_WithNoSchema_ShouldShowDefaultFields()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act - select a root with no schema
        vm.SelectedProjectRoot = CreateDefaultRoot();

        // Assert
        vm.FormFields.Should().HaveCount(2);
        vm.FormFields[0].Key.Should().Be("name");
        vm.FormFields[0].Title.Should().Be("Project Name");
        vm.FormFields[0].IsRequired.Should().BeTrue();
        vm.FormFields[1].Key.Should().Be("prompt");
        vm.FormFields[1].Title.Should().Be("Initial Prompt");
        vm.FormFields[1].IsMultiline.Should().BeTrue();
    }

    [Fact]
    public void SelectingProjectRoot_WithCustomSchema_ShouldShowCustomFields()
    {
        // Arrange
        var vm = CreateViewModel();
        var schemaJson = """
        {
            "type": "object",
            "properties": {
                "caseId": { "type": "string", "title": "Case ID" },
                "prompt": { "type": "string", "title": "Instructions", "x-multiline": true, "default": "Fix it" }
            },
            "required": ["caseId"]
        }
        """;
        var schema = JsonSerializer.Deserialize<JsonElement>(schemaJson);

        // Act
        vm.SelectedProjectRoot = CreateDefaultRoot(schema: schema);

        // Assert
        vm.FormFields.Should().HaveCount(2);
        vm.FormFields[0].Key.Should().Be("caseId");
        vm.FormFields[0].Title.Should().Be("Case ID");
        vm.FormFields[0].IsRequired.Should().BeTrue();
        vm.FormFields[0].IsMultiline.Should().BeFalse();
        vm.FormFields[1].Key.Should().Be("prompt");
        vm.FormFields[1].Title.Should().Be("Instructions");
        vm.FormFields[1].IsMultiline.Should().BeTrue();
        vm.FormFields[1].Value.Should().Be("Fix it");
    }

    [Fact]
    public void SelectingProjectRoot_WithNull_ShouldClearFields()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.SelectedProjectRoot = CreateDefaultRoot();
        vm.FormFields.Should().HaveCount(2);

        // Act
        vm.SelectedProjectRoot = null;

        // Assert
        vm.FormFields.Should().BeEmpty();
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

        var roots = new[]
        {
            new ProjectRootInfo("default", "Default root"),
            new ProjectRootInfo("work", "Work projects")
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
    public async Task CreateCommand_WhenNoProjectRootSelected_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.SelectedProjectRoot = null;

        // Act
        await vm.CreateCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Be("Please select a project root");
    }

    [Fact]
    public async Task CreateCommand_WhenRequiredFieldEmpty_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.SelectedProjectRoot = CreateDefaultRoot();
        // FormFields auto-populated: name (required), prompt (required)
        // Leave name empty

        // Act
        await vm.CreateCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Be("Please fill in Project Name");
        await ProjectService.DidNotReceive().CreateProjectAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Dictionary<string, JsonElement>>());
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
        vm.SelectedProjectRoot = CreateDefaultRoot();

        // Fill in form fields
        vm.FormFields[0].Value = "MyProject";  // name
        vm.FormFields[1].Value = "Build a web app";  // prompt

        var status = new ProjectStatus(
            "MyProject", "MyProject", ProjectState.Running,
            DateTime.UtcNow, DateTime.UtcNow, null,
            new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0);
        var detail = new ProjectDetail(status, "session123");

        ProjectService.CreateProjectAsync("TestProfile", "host1", "default",
            Arg.Any<Dictionary<string, JsonElement>>())
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
            "TestProfile", "host1", "default",
            Arg.Is<Dictionary<string, JsonElement>>(d =>
                d.ContainsKey("name") && d.ContainsKey("prompt")));
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
        vm.SelectedProjectRoot = CreateDefaultRoot();
        vm.FormFields[0].Value = "MyProject";
        vm.FormFields[1].Value = "Build a web app";

        ProjectService.CreateProjectAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<Dictionary<string, JsonElement>>())
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
        vm.SelectedProjectRoot = CreateDefaultRoot();
        vm.FormFields[0].Value = "MyProject";
        vm.FormFields[1].Value = "Build a web app";

        var creatingStates = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CreateProjectViewModel.IsCreating))
                creatingStates.Add(vm.IsCreating);
        };

        var status = new ProjectStatus(
            "MyProject", "MyProject", ProjectState.Running,
            DateTime.UtcNow, DateTime.UtcNow, null,
            new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), null, null, 0);
        var detail = new ProjectDetail(status, "session123");

        ProjectService.CreateProjectAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<Dictionary<string, JsonElement>>())
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
