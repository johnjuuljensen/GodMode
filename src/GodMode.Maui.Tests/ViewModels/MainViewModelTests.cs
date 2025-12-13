using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using GodMode.Maui.ViewModels;
using GodMode.Shared.Models;
using GodMode.Shared.Enums;
using Profile = GodMode.Maui.Services.Models.Profile;

namespace GodMode.Maui.Tests.ViewModels;

public class MainViewModelTests : TestBase
{
    private MainViewModel CreateViewModel() =>
        new(ProfileService, HostConnectionService, NotificationService);

    #region Initial State Tests

    [Fact]
    public void Constructor_ShouldInitializeWithDefaultValues()
    {
        // Act
        var vm = CreateViewModel();

        // Assert
        vm.Profiles.Should().BeEmpty();
        vm.SelectedProfile.Should().BeNull();
        vm.Hosts.Should().BeEmpty();
        vm.IsLoading.Should().BeFalse();
        vm.ErrorMessage.Should().BeNull();
    }

    #endregion

    #region LoadAsync Tests

    [Fact]
    public async Task LoadCommand_WhenProfilesExist_ShouldLoadProfilesAndHosts()
    {
        // Arrange
        var vm = CreateViewModel();
        var profiles = new List<Profile>
        {
            new() { Name = "TestProfile", Accounts = [] }
        };
        var hosts = new List<HostInfo>
        {
            new("host1", "Host 1", "local", HostState.Running)
        };

        ProfileService.GetProfilesAsync().Returns(Task.FromResult(profiles));
        ProfileService.GetSelectedProfileAsync().Returns(Task.FromResult<Profile?>(profiles[0]));
        HostConnectionService.ListAllHostsAsync("TestProfile").Returns(Task.FromResult<IEnumerable<HostInfo>>(hosts));

        // Act
        await vm.LoadCommand.ExecuteAsync(null);

        // Assert
        vm.Profiles.Should().HaveCount(1);
        vm.SelectedProfile.Should().NotBeNull();
        vm.SelectedProfile!.Name.Should().Be("TestProfile");
        vm.Hosts.Should().HaveCount(1);
        vm.IsLoading.Should().BeFalse();
        vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task LoadCommand_WhenNoProfiles_ShouldHaveEmptyProfiles()
    {
        // Arrange
        var vm = CreateViewModel();
        ProfileService.GetProfilesAsync().Returns(Task.FromResult(new List<Profile>()));

        // Act
        await vm.LoadCommand.ExecuteAsync(null);

        // Assert
        vm.Profiles.Should().BeEmpty();
        vm.SelectedProfile.Should().BeNull();
        vm.Hosts.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadCommand_WhenExceptionThrown_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        ProfileService.GetProfilesAsync().ThrowsAsync(new Exception("Test error"));

        // Act
        await vm.LoadCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Contain("Test error");
        vm.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task LoadCommand_ShouldSetIsLoadingDuringExecution()
    {
        // Arrange
        var vm = CreateViewModel();
        var loadingStates = new List<bool>();

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsLoading))
                loadingStates.Add(vm.IsLoading);
        };

        ProfileService.GetProfilesAsync().Returns(Task.FromResult(new List<Profile>()));

        // Act
        await vm.LoadCommand.ExecuteAsync(null);

        // Assert - Should have been true then false
        loadingStates.Should().Contain(true);
        loadingStates.Last().Should().BeFalse();
    }

    #endregion

    #region RefreshHostsAsync Tests

    [Fact]
    public async Task RefreshHostsCommand_WhenNoSelectedProfile_ShouldDoNothing()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.SelectedProfile.Should().BeNull();

        // Act
        await vm.RefreshHostsCommand.ExecuteAsync(null);

        // Assert
        await HostConnectionService.DidNotReceive().ListAllHostsAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task RefreshHostsCommand_WhenProfileSelected_ShouldRefreshHosts()
    {
        // Arrange
        var vm = CreateViewModel();
        var profile = new Profile { Name = "TestProfile", Accounts = [] };
        var hosts = new List<HostInfo>
        {
            new("host1", "Host 1", "local", HostState.Running),
            new("host2", "Host 2", "github", HostState.Stopped)
        };

        // Setup initial state
        ProfileService.GetProfilesAsync().Returns(Task.FromResult(new List<Profile> { profile }));
        ProfileService.GetSelectedProfileAsync().Returns(Task.FromResult<Profile?>(profile));
        HostConnectionService.ListAllHostsAsync("TestProfile").Returns(Task.FromResult<IEnumerable<HostInfo>>(hosts));

        await vm.LoadCommand.ExecuteAsync(null);

        // Act
        await vm.RefreshHostsCommand.ExecuteAsync(null);

        // Assert
        vm.Hosts.Should().HaveCount(2);
        await HostConnectionService.Received().ListAllHostsAsync("TestProfile"); // At least one refresh call
    }

    #endregion

    #region SelectProfileAsync Tests

    [Fact]
    public async Task SelectProfileCommand_ShouldUpdateSelectedProfileAndLoadHosts()
    {
        // Arrange
        var vm = CreateViewModel();
        var profile = new Profile { Name = "NewProfile", Accounts = [] };
        var hosts = new List<HostInfo>
        {
            new("host1", "Host 1", "local", HostState.Running)
        };

        HostConnectionService.ListAllHostsAsync("NewProfile").Returns(Task.FromResult<IEnumerable<HostInfo>>(hosts));

        // Act
        await vm.SelectProfileCommand.ExecuteAsync(profile);

        // Assert
        vm.SelectedProfile.Should().Be(profile);
        await ProfileService.Received(1).SetSelectedProfileAsync("NewProfile");
    }

    [Fact]
    public async Task SelectProfileCommand_WithNullProfile_ShouldDoNothing()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        await vm.SelectProfileCommand.ExecuteAsync(null!);

        // Assert
        await ProfileService.DidNotReceive().SetSelectedProfileAsync(Arg.Any<string>());
    }

    #endregion

    #region GetBadgeCount Tests

    [Fact]
    public void GetBadgeCount_WhenNoSelectedProfile_ShouldReturnZero()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        var count = vm.GetBadgeCount("host1");

        // Assert
        count.Should().Be(0);
    }

    [Fact]
    public async Task GetBadgeCount_WhenProfileSelected_ShouldReturnBadgeCount()
    {
        // Arrange
        var vm = CreateViewModel();
        var profile = new Profile { Name = "TestProfile", Accounts = [] };

        ProfileService.GetProfilesAsync().Returns(Task.FromResult(new List<Profile> { profile }));
        ProfileService.GetSelectedProfileAsync().Returns(Task.FromResult<Profile?>(profile));
        HostConnectionService.ListAllHostsAsync("TestProfile").Returns(Task.FromResult<IEnumerable<HostInfo>>(new List<HostInfo>()));
        NotificationService.GetBadgeCount("TestProfile", "host1").Returns(5);

        await vm.LoadCommand.ExecuteAsync(null);

        // Act
        var count = vm.GetBadgeCount("host1");

        // Assert
        count.Should().Be(5);
    }

    #endregion

    #region Property Change Notification Tests

    [Fact]
    public async Task SelectedProfile_WhenChanged_ShouldTriggerHostsLoad()
    {
        // Arrange
        var vm = CreateViewModel();
        var profile = new Profile { Name = "TestProfile", Accounts = [] };

        HostConnectionService.ListAllHostsAsync("TestProfile").Returns(Task.FromResult<IEnumerable<HostInfo>>(new List<HostInfo>()));

        // Act
        vm.SelectedProfile = profile;

        // Assert - Give async operation time to complete
        await Task.Delay(100);
        await HostConnectionService.Received().ListAllHostsAsync("TestProfile");
    }

    #endregion
}
