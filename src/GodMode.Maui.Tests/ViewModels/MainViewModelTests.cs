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
        vm.ProfileOptions.Should().BeEmpty();
        vm.SelectedProfileOption.Should().BeNull();
        vm.Servers.Should().BeEmpty();
        vm.IsLoading.Should().BeFalse();
        vm.ErrorMessage.Should().BeNull();
    }

    #endregion

    #region LoadAsync Tests

    [Fact]
    public async Task LoadCommand_WhenProfilesExist_ShouldLoadProfilesAndServers()
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
        HostConnectionService.IsConnected("TestProfile", "host1").Returns(false);

        // Act
        await vm.LoadCommand.ExecuteAsync(null);

        // Assert
        vm.ProfileOptions.Should().HaveCount(2); // "All" + 1 profile
        vm.SelectedProfileOption.Should().NotBeNull();
        vm.SelectedProfileOption!.Name.Should().Be("TestProfile");
        vm.Servers.Should().HaveCount(1);
        vm.IsLoading.Should().BeFalse();
        vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task LoadCommand_WhenNoProfiles_ShouldHaveOnlyAllOption()
    {
        // Arrange
        var vm = CreateViewModel();
        ProfileService.GetProfilesAsync().Returns(Task.FromResult(new List<Profile>()));
        ProfileService.GetSelectedProfileAsync().Returns(Task.FromResult<Profile?>(null));

        // Act
        await vm.LoadCommand.ExecuteAsync(null);

        // Assert
        vm.ProfileOptions.Should().HaveCount(1); // Only "All"
        vm.ProfileOptions[0].Name.Should().Be("All");
        vm.SelectedProfileOption.Should().Be(MainViewModel.AllProfilesOption);
        vm.Servers.Should().BeEmpty();
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
        ProfileService.GetSelectedProfileAsync().Returns(Task.FromResult<Profile?>(null));

        // Act
        await vm.LoadCommand.ExecuteAsync(null);

        // Assert - Should have been true then false
        loadingStates.Should().Contain(true);
        loadingStates.Last().Should().BeFalse();
    }

    #endregion

    #region RefreshAsync Tests

    [Fact]
    public async Task RefreshCommand_WhenAllSelected_ShouldLoadServersFromAllProfiles()
    {
        // Arrange
        var vm = CreateViewModel();
        var profiles = new List<Profile>
        {
            new() { Name = "Profile1", Accounts = [] },
            new() { Name = "Profile2", Accounts = [] }
        };
        var hosts1 = new List<HostInfo> { new("host1", "Host 1", "local", HostState.Running) };
        var hosts2 = new List<HostInfo> { new("host2", "Host 2", "github", HostState.Stopped) };

        ProfileService.GetProfilesAsync().Returns(Task.FromResult(profiles));
        ProfileService.GetSelectedProfileAsync().Returns(Task.FromResult<Profile?>(null)); // No previous selection
        HostConnectionService.ListAllHostsAsync("Profile1").Returns(Task.FromResult<IEnumerable<HostInfo>>(hosts1));
        HostConnectionService.ListAllHostsAsync("Profile2").Returns(Task.FromResult<IEnumerable<HostInfo>>(hosts2));
        HostConnectionService.IsConnected(Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        await vm.LoadCommand.ExecuteAsync(null);
        vm.SelectedProfileOption = MainViewModel.AllProfilesOption;

        // Act
        await vm.RefreshCommand.ExecuteAsync(null);

        // Assert
        vm.Servers.Should().HaveCount(2);
    }

    [Fact]
    public async Task RefreshCommand_WhenProfileSelected_ShouldRefreshServers()
    {
        // Arrange
        var vm = CreateViewModel();
        var profile = new Profile { Name = "TestProfile", Accounts = [] };
        var hosts = new List<HostInfo>
        {
            new("host1", "Host 1", "local", HostState.Running),
            new("host2", "Host 2", "github", HostState.Stopped)
        };

        ProfileService.GetProfilesAsync().Returns(Task.FromResult(new List<Profile> { profile }));
        ProfileService.GetSelectedProfileAsync().Returns(Task.FromResult<Profile?>(profile));
        HostConnectionService.ListAllHostsAsync("TestProfile").Returns(Task.FromResult<IEnumerable<HostInfo>>(hosts));
        HostConnectionService.IsConnected(Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        await vm.LoadCommand.ExecuteAsync(null);

        // Act
        await vm.RefreshCommand.ExecuteAsync(null);

        // Assert
        vm.Servers.Should().HaveCount(2);
        await HostConnectionService.Received().ListAllHostsAsync("TestProfile");
    }

    #endregion

    #region GetBadgeCount Tests

    [Fact]
    public void GetBadgeCount_ShouldReturnBadgeCount()
    {
        // Arrange
        var vm = CreateViewModel();
        NotificationService.GetBadgeCount("TestProfile", "host1").Returns(5);

        // Act
        var count = vm.GetBadgeCount("TestProfile", "host1");

        // Assert
        count.Should().Be(5);
    }

    #endregion

    #region IsAllProfilesSelected Tests

    [Fact]
    public async Task IsAllProfilesSelected_WhenAllOptionSelected_ShouldReturnTrue()
    {
        // Arrange
        var vm = CreateViewModel();
        ProfileService.GetProfilesAsync().Returns(Task.FromResult(new List<Profile>()));
        ProfileService.GetSelectedProfileAsync().Returns(Task.FromResult<Profile?>(null));

        await vm.LoadCommand.ExecuteAsync(null);

        // Act & Assert
        vm.SelectedProfileOption = MainViewModel.AllProfilesOption;
        vm.IsAllProfilesSelected.Should().BeTrue();
        vm.SelectedProfile.Should().BeNull();
    }

    [Fact]
    public async Task IsAllProfilesSelected_WhenSpecificProfileSelected_ShouldReturnFalse()
    {
        // Arrange
        var vm = CreateViewModel();
        var profile = new Profile { Name = "TestProfile", Accounts = [] };
        ProfileService.GetProfilesAsync().Returns(Task.FromResult(new List<Profile> { profile }));
        ProfileService.GetSelectedProfileAsync().Returns(Task.FromResult<Profile?>(profile));
        HostConnectionService.ListAllHostsAsync("TestProfile").Returns(Task.FromResult<IEnumerable<HostInfo>>(new List<HostInfo>()));

        await vm.LoadCommand.ExecuteAsync(null);

        // Act & Assert
        vm.IsAllProfilesSelected.Should().BeFalse();
        vm.SelectedProfile.Should().NotBeNull();
        vm.SelectedProfile!.Name.Should().Be("TestProfile");
    }

    #endregion

    #region Property Change Notification Tests

    [Fact]
    public async Task SelectedProfileOption_WhenChanged_ShouldTriggerServersLoad()
    {
        // Arrange
        var vm = CreateViewModel();
        var profile = new Profile { Name = "TestProfile", Accounts = [] };
        var profiles = new List<Profile> { profile };

        ProfileService.GetProfilesAsync().Returns(Task.FromResult(profiles));
        ProfileService.GetSelectedProfileAsync().Returns(Task.FromResult<Profile?>(null));
        HostConnectionService.ListAllHostsAsync("TestProfile").Returns(Task.FromResult<IEnumerable<HostInfo>>(new List<HostInfo>()));
        HostConnectionService.IsConnected(Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        await vm.LoadCommand.ExecuteAsync(null);

        // Act - change from "All" to specific profile
        vm.SelectedProfileOption = profile;

        // Assert - Give async operation time to complete
        await Task.Delay(100);
        await HostConnectionService.Received().ListAllHostsAsync("TestProfile");
    }

    #endregion
}
