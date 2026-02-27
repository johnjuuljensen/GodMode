using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using GodMode.Maui.ViewModels;
using GodMode.Shared.Models;
using GodMode.Shared.Enums;
using System.Reactive.Subjects;
using System.Reactive.Linq;

namespace GodMode.Maui.Tests.ViewModels;

public class ProjectViewModelTests : TestBase
{
    private ProjectViewModel CreateViewModel() =>
        new(ProjectService, NotificationService);

    #region Initial State Tests

    [Fact]
    public void Constructor_ShouldInitializeWithDefaultValues()
    {
        // Act
        var vm = CreateViewModel();

        // Assert
        vm.ProfileName.Should().BeEmpty();
        vm.HostId.Should().BeEmpty();
        vm.ProjectId.Should().BeEmpty();
        vm.Status.Should().BeNull();
        vm.OutputMessages.Should().BeEmpty();
        vm.InputText.Should().BeEmpty();
        vm.IsLoading.Should().BeFalse();
        vm.ErrorMessage.Should().BeNull();
        vm.CanSendInput.Should().BeFalse();
        vm.ShowMetrics.Should().BeFalse();
        vm.MetricsHtml.Should().BeNull();
    }

    #endregion

    #region LoadAsync Tests

    [Fact]
    public async Task LoadCommand_WhenMissingProfileName_ShouldDoNothing()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "";
        vm.HostId = "host1";
        vm.ProjectId = "proj1";

        // Act
        await vm.LoadCommand.ExecuteAsync(null);

        // Assert
        await ProjectService.DidNotReceive().GetStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task LoadCommand_WhenMissingHostId_ShouldDoNothing()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "";
        vm.ProjectId = "proj1";

        // Act
        await vm.LoadCommand.ExecuteAsync(null);

        // Assert
        await ProjectService.DidNotReceive().GetStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task LoadCommand_WhenMissingProjectId_ShouldDoNothing()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectId = "";

        // Act
        await vm.LoadCommand.ExecuteAsync(null);

        // Assert
        await ProjectService.DidNotReceive().GetStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task LoadCommand_WhenValidInput_ShouldLoadStatusAndSubscribe()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectId = "proj1";

        var status = new ProjectStatus(
            "proj1", "Project 1", ProjectState.Running,
            DateTime.UtcNow, DateTime.UtcNow, null,
            new ProjectMetrics(100, 200, 5, TimeSpan.FromMinutes(1), 0.05m),
            null, null, 0);

        var outputSubject = new Subject<ClaudeMessage>();

        ProjectService.GetStatusAsync("TestProfile", "host1", "proj1", false).Returns(Task.FromResult(status));
        ProjectService.SubscribeOutputAsync("TestProfile", "host1", "proj1", 0)
            .Returns(Task.FromResult(outputSubject.AsObservable()));

        // Act
        await vm.LoadCommand.ExecuteAsync(null);

        // Assert
        vm.Status.Should().NotBeNull();
        vm.Status!.Name.Should().Be("Project 1");
        vm.IsLoading.Should().BeFalse();
        vm.ErrorMessage.Should().BeNull();
        NotificationService.Received(1).ClearBadgeCountForProject("TestProfile", "host1", "proj1");
    }

    [Fact]
    public async Task LoadCommand_WhenStatusIsWaitingInput_ShouldEnableInput()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectId = "proj1";

        var status = new ProjectStatus(
            "proj1", "Project 1", ProjectState.WaitingInput,
            DateTime.UtcNow, DateTime.UtcNow, "What should I do?",
            new ProjectMetrics(100, 200, 5, TimeSpan.FromMinutes(1), 0.05m),
            null, null, 0);

        var outputSubject = new Subject<ClaudeMessage>();

        ProjectService.GetStatusAsync("TestProfile", "host1", "proj1", false).Returns(Task.FromResult(status));
        ProjectService.SubscribeOutputAsync("TestProfile", "host1", "proj1", 0)
            .Returns(Task.FromResult(outputSubject.AsObservable()));

        // Act
        await vm.LoadCommand.ExecuteAsync(null);

        // Assert
        vm.CanSendInput.Should().BeTrue();
    }

    [Fact]
    public async Task LoadCommand_WhenExceptionThrown_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectId = "proj1";

        ProjectService.GetStatusAsync("TestProfile", "host1", "proj1", false)
            .ThrowsAsync(new Exception("Failed to load"));

        // Act
        await vm.LoadCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Contain("Failed to load");
        vm.IsLoading.Should().BeFalse();
    }

    #endregion

    #region RefreshAsync Tests

    [Fact]
    public async Task RefreshCommand_ShouldForceRefreshStatus()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectId = "proj1";

        var status = new ProjectStatus(
            "proj1", "Project 1", ProjectState.Running,
            DateTime.UtcNow, DateTime.UtcNow, null,
            new ProjectMetrics(100, 200, 5, TimeSpan.FromMinutes(1), 0.05m),
            null, null, 0);

        ProjectService.GetStatusAsync("TestProfile", "host1", "proj1", true).Returns(Task.FromResult(status));

        // Act
        await vm.RefreshCommand.ExecuteAsync(null);

        // Assert
        await ProjectService.Received(1).GetStatusAsync("TestProfile", "host1", "proj1", true);
    }

    #endregion

    #region SendInputAsync Tests

    [Fact]
    public async Task SendInputCommand_WhenInputEmpty_ShouldDoNothing()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.InputText = "";
        vm.CanSendInput = true;

        // Act
        await vm.SendInputCommand.ExecuteAsync(null);

        // Assert
        await ProjectService.DidNotReceive().SendInputAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SendInputCommand_WhenCannotSendInput_ShouldDoNothing()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.InputText = "Some input";
        vm.CanSendInput = false;

        // Act
        await vm.SendInputCommand.ExecuteAsync(null);

        // Assert
        await ProjectService.DidNotReceive().SendInputAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SendInputCommand_WhenValid_ShouldSendInputAndAddToOutput()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectId = "proj1";
        vm.InputText = "User response";
        vm.CanSendInput = true;

        var status = new ProjectStatus(
            "proj1", "Project 1", ProjectState.Running,
            DateTime.UtcNow, DateTime.UtcNow, null,
            new ProjectMetrics(100, 200, 5, TimeSpan.FromMinutes(1), 0.05m),
            null, null, 0);

        ProjectService.GetStatusAsync("TestProfile", "host1", "proj1", true).Returns(Task.FromResult(status));

        // Act
        await vm.SendInputCommand.ExecuteAsync(null);

        // Assert
        await ProjectService.Received(1).SendInputAsync("TestProfile", "host1", "proj1", "User response");
        vm.InputText.Should().BeEmpty(); // Input should be cleared
        vm.OutputMessages.Should().HaveCount(1);
        vm.OutputMessages[0].Type.Should().Be("user");
        vm.OutputMessages[0].ContentItems.Should().Contain(c => c.Type == "text" && c.Summary.Contains("User response"));
    }

    [Fact]
    public async Task SendInputCommand_WhenExceptionThrown_ShouldRestoreInputAndSetError()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectId = "proj1";
        vm.InputText = "User response";
        vm.CanSendInput = true;

        ProjectService.SendInputAsync("TestProfile", "host1", "proj1", "User response")
            .ThrowsAsync(new Exception("Send failed"));

        // Act
        await vm.SendInputCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Contain("Send failed");
        vm.InputText.Should().Be("User response"); // Input should be restored
    }

    #endregion

    #region StopProjectAsync Tests

    [Fact]
    public async Task StopProjectCommand_ShouldStopProjectAndRefresh()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectId = "proj1";

        var status = new ProjectStatus(
            "proj1", "Project 1", ProjectState.Stopped,
            DateTime.UtcNow, DateTime.UtcNow, null,
            new ProjectMetrics(100, 200, 5, TimeSpan.FromMinutes(1), 0.05m),
            null, null, 0);

        ProjectService.GetStatusAsync("TestProfile", "host1", "proj1", true).Returns(Task.FromResult(status));

        // Act
        await vm.StopProjectCommand.ExecuteAsync(null);

        // Assert
        await ProjectService.Received(1).StopProjectAsync("TestProfile", "host1", "proj1");
        await ProjectService.Received(1).GetStatusAsync("TestProfile", "host1", "proj1", true);
        vm.IsLoading.Should().BeFalse();
    }

    #endregion

    #region LoadMetricsAsync Tests

    [Fact]
    public async Task LoadMetricsCommand_ShouldLoadMetricsHtmlAndShowMetrics()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectId = "proj1";

        var metricsHtml = "<html><body>Metrics</body></html>";
        ProjectService.GetMetricsHtmlAsync("TestProfile", "host1", "proj1").Returns(Task.FromResult(metricsHtml));

        // Act
        await vm.LoadMetricsCommand.ExecuteAsync(null);

        // Assert
        vm.MetricsHtml.Should().Be(metricsHtml);
        vm.ShowMetrics.Should().BeTrue();
        vm.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task LoadMetricsCommand_WhenExceptionThrown_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.HostId = "host1";
        vm.ProjectId = "proj1";

        ProjectService.GetMetricsHtmlAsync("TestProfile", "host1", "proj1")
            .ThrowsAsync(new Exception("Metrics failed"));

        // Act
        await vm.LoadMetricsCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Contain("Metrics failed");
        vm.ShowMetrics.Should().BeFalse();
    }

    #endregion

    #region CloseMetrics Tests

    [Fact]
    public void CloseMetricsCommand_ShouldHideMetrics()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ShowMetrics = true;

        // Act
        vm.CloseMetricsCommand.Execute(null);

        // Assert
        vm.ShowMetrics.Should().BeFalse();
    }

    #endregion

    #region Status Change Tests

    [Fact]
    public void OnStatusChanged_WhenWaitingInput_ShouldEnableInput()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.Status = new ProjectStatus(
            "proj1", "Project 1", ProjectState.WaitingInput,
            DateTime.UtcNow, DateTime.UtcNow, null,
            new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0),
            null, null, 0);

        // Assert
        vm.CanSendInput.Should().BeTrue();
    }

    [Fact]
    public void OnStatusChanged_WhenRunning_ShouldEnableInput()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.Status = new ProjectStatus(
            "proj1", "Project 1", ProjectState.Running,
            DateTime.UtcNow, DateTime.UtcNow, null,
            new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0),
            null, null, 0);

        // Assert (Running allows input for interrupts)
        vm.CanSendInput.Should().BeTrue();
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_ShouldDisposeOutputSubscription()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act & Assert - should not throw
        vm.Dispose();
    }

    #endregion
}
