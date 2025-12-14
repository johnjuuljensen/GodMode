using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using GodMode.Maui.ViewModels;

namespace GodMode.Maui.Tests.ViewModels;

public class AddAccountViewModelTests : TestBase
{
    private AddAccountViewModel CreateViewModel() =>
        new(ProfileService);

    #region Initial State Tests

    [Fact]
    public void Constructor_ShouldInitializeWithDefaultValues()
    {
        // Act
        var vm = CreateViewModel();

        // Assert
        vm.ProfileName.Should().BeEmpty();
        vm.SelectedAccountType.Should().Be("Local Server");
        vm.GitHubUsername.Should().BeEmpty();
        vm.GitHubToken.Should().BeEmpty();
        vm.ServerUrl.Should().Be("http://localhost:5000");
        vm.ServerDisplayName.Should().BeEmpty();
        vm.IsSaving.Should().BeFalse();
        vm.ErrorMessage.Should().BeNull();
        vm.AccountTypes.Should().Contain("GitHub Codespaces");
        vm.AccountTypes.Should().Contain("Local Server");
    }

    [Fact]
    public void IsGitHubAccount_WhenSelectedTypeIsGitHub_ShouldReturnTrue()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.SelectedAccountType = "GitHub Codespaces";

        // Assert
        vm.IsGitHubAccount.Should().BeTrue();
        vm.IsLocalServer.Should().BeFalse();
    }

    [Fact]
    public void IsLocalServer_WhenSelectedTypeIsLocal_ShouldReturnTrue()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.SelectedAccountType = "Local Server";

        // Assert
        vm.IsLocalServer.Should().BeTrue();
        vm.IsGitHubAccount.Should().BeFalse();
    }

    #endregion

    #region SaveAsync Validation Tests - No Profile

    [Fact]
    public async Task SaveCommand_WhenProfileNameEmpty_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "";

        // Act
        await vm.SaveCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Be("No profile selected");
        await ProfileService.DidNotReceive().SaveProfileAsync(Arg.Any<Profile>());
    }

    #endregion

    #region SaveAsync Validation Tests - GitHub Account

    [Fact]
    public async Task SaveCommand_GitHubAccount_WhenUsernameEmpty_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.SelectedAccountType = "GitHub Codespaces";
        vm.GitHubUsername = "";
        vm.GitHubToken = "some-token";

        // Act
        await vm.SaveCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Be("Please enter your GitHub username");
    }

    [Fact]
    public async Task SaveCommand_GitHubAccount_WhenTokenEmpty_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.SelectedAccountType = "GitHub Codespaces";
        vm.GitHubUsername = "testuser";
        vm.GitHubToken = "";

        // Act
        await vm.SaveCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Be("Please enter your GitHub personal access token");
    }

    #endregion

    #region SaveAsync Validation Tests - Local Server

    [Fact]
    public async Task SaveCommand_LocalServer_WhenUrlEmpty_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.SelectedAccountType = "Local Server";
        vm.ServerUrl = "";

        // Act
        await vm.SaveCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Be("Please enter a server URL");
    }

    [Fact]
    public async Task SaveCommand_LocalServer_WhenUrlInvalid_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.SelectedAccountType = "Local Server";
        vm.ServerUrl = "not-a-url";

        // Act
        await vm.SaveCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Be("Server URL must start with http:// or https://");
    }

    #endregion

    #region SaveAsync Profile Not Found Tests

    [Fact]
    public async Task SaveCommand_WhenProfileNotFound_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "NonExistentProfile";
        vm.SelectedAccountType = "GitHub Codespaces";
        vm.GitHubUsername = "testuser";
        vm.GitHubToken = "testtoken";

        ProfileService.GetProfileAsync("NonExistentProfile")
            .Returns(Task.FromResult<Profile?>(null));

        // Act
        await vm.SaveCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Be("Profile not found");
    }

    #endregion

    #region SaveAsync Success Tests

    [Fact]
    public async Task SaveCommand_GitHubAccount_WhenValid_ShouldAddAccountToProfile()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.SelectedAccountType = "GitHub Codespaces";
        vm.GitHubUsername = "testuser";
        vm.GitHubToken = "testtoken";

        var profile = new Profile { Name = "TestProfile", Accounts = [] };
        ProfileService.GetProfileAsync("TestProfile")
            .Returns(Task.FromResult<Profile?>(profile));

        Profile? savedProfile = null;
        ProfileService.SaveProfileAsync(Arg.Do<Profile>(p => savedProfile = p))
            .Returns(Task.CompletedTask);

        // Act - Shell.Current will throw, but we can verify SaveProfileAsync was called
        try
        {
            await vm.SaveCommand.ExecuteAsync(null);
        }
        catch (NullReferenceException)
        {
            // Expected - Shell.Current is null in tests
        }

        // Assert
        savedProfile.Should().NotBeNull();
        savedProfile!.Accounts.Should().HaveCount(1);
        savedProfile.Accounts[0].Type.Should().Be("github");
        savedProfile.Accounts[0].Username.Should().Be("testuser");
        savedProfile.Accounts[0].Token.Should().Be("testtoken");
    }

    [Fact]
    public async Task SaveCommand_LocalServer_WhenValid_ShouldAddAccountToProfile()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.SelectedAccountType = "Local Server";
        vm.ServerUrl = "http://localhost:5000";

        var profile = new Profile { Name = "TestProfile", Accounts = [] };
        ProfileService.GetProfileAsync("TestProfile")
            .Returns(Task.FromResult<Profile?>(profile));

        Profile? savedProfile = null;
        ProfileService.SaveProfileAsync(Arg.Do<Profile>(p => savedProfile = p))
            .Returns(Task.CompletedTask);

        // Act - Shell.Current will throw, but we can verify SaveProfileAsync was called
        try
        {
            await vm.SaveCommand.ExecuteAsync(null);
        }
        catch (NullReferenceException)
        {
            // Expected - Shell.Current is null in tests
        }

        // Assert
        savedProfile.Should().NotBeNull();
        savedProfile!.Accounts.Should().HaveCount(1);
        savedProfile.Accounts[0].Type.Should().Be("local");
        savedProfile.Accounts[0].Path.Should().Be("http://localhost:5000");
    }

    [Fact]
    public async Task SaveCommand_LocalServer_WithDisplayName_ShouldIncludeMetadata()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.SelectedAccountType = "Local Server";
        vm.ServerUrl = "http://localhost:5000";
        vm.ServerDisplayName = "My Server";

        var profile = new Profile { Name = "TestProfile", Accounts = [] };
        ProfileService.GetProfileAsync("TestProfile")
            .Returns(Task.FromResult<Profile?>(profile));

        Profile? savedProfile = null;
        ProfileService.SaveProfileAsync(Arg.Do<Profile>(p => savedProfile = p))
            .Returns(Task.CompletedTask);

        // Act - Shell.Current will throw, but we can verify SaveProfileAsync was called
        try
        {
            await vm.SaveCommand.ExecuteAsync(null);
        }
        catch (NullReferenceException)
        {
            // Expected - Shell.Current is null in tests
        }

        // Assert
        savedProfile.Should().NotBeNull();
        savedProfile!.Accounts.Should().HaveCount(1);
        savedProfile.Accounts[0].Metadata.Should().ContainKey("name");
        savedProfile.Accounts[0].Metadata!["name"].Should().Be("My Server");
    }

    #endregion

    #region SaveAsync Error Handling Tests

    [Fact]
    public async Task SaveCommand_WhenExceptionThrown_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.SelectedAccountType = "GitHub Codespaces";
        vm.GitHubUsername = "testuser";
        vm.GitHubToken = "testtoken";

        var profile = new Profile { Name = "TestProfile", Accounts = [] };
        ProfileService.GetProfileAsync("TestProfile")
            .Returns(Task.FromResult<Profile?>(profile));
        ProfileService.SaveProfileAsync(Arg.Any<Profile>())
            .ThrowsAsync(new Exception("Save failed"));

        // Act
        await vm.SaveCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Contain("Save failed");
        vm.IsSaving.Should().BeFalse();
    }

    #endregion

    #region IsSaving State Tests

    [Fact]
    public async Task SaveCommand_ShouldSetIsSavingDuringExecution()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.SelectedAccountType = "GitHub Codespaces";
        vm.GitHubUsername = "testuser";
        vm.GitHubToken = "testtoken";

        var savingStates = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AddAccountViewModel.IsSaving))
                savingStates.Add(vm.IsSaving);
        };

        var profile = new Profile { Name = "TestProfile", Accounts = [] };
        ProfileService.GetProfileAsync("TestProfile")
            .Returns(Task.FromResult<Profile?>(profile));
        ProfileService.SaveProfileAsync(Arg.Any<Profile>())
            .Returns(async _ =>
            {
                await Task.Delay(10);
                throw new NullReferenceException(); // Shell.Current throws
            });

        // Act
        await vm.SaveCommand.ExecuteAsync(null);

        // Assert - Should have been true then false
        savingStates.Should().Contain(true);
        savingStates.Last().Should().BeFalse();
    }

    #endregion
}
