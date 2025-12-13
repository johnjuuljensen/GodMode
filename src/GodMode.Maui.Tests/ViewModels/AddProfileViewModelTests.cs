using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using GodMode.Maui.ViewModels;
using GodMode.Maui.Services.Models;

namespace GodMode.Maui.Tests.ViewModels;

public class AddProfileViewModelTests : TestBase
{
    private AddProfileViewModel CreateViewModel() =>
        new(ProfileService);

    #region Initial State Tests

    [Fact]
    public void Constructor_ShouldInitializeWithDefaultValues()
    {
        // Act
        var vm = CreateViewModel();

        // Assert
        vm.ProfileName.Should().BeEmpty();
        vm.SelectedAccountType.Should().Be("Local Folder");
        vm.GitHubUsername.Should().BeEmpty();
        vm.GitHubToken.Should().BeEmpty();
        vm.LocalPath.Should().BeEmpty();
        vm.LocalDisplayName.Should().BeEmpty();
        vm.IsSaving.Should().BeFalse();
        vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Constructor_ShouldHaveTwoAccountTypes()
    {
        // Act
        var vm = CreateViewModel();

        // Assert
        vm.AccountTypes.Should().HaveCount(2);
        vm.AccountTypes.Should().Contain("GitHub Codespaces");
        vm.AccountTypes.Should().Contain("Local Folder");
    }

    #endregion

    #region IsGitHubAccount / IsLocalAccount Tests

    [Fact]
    public void IsGitHubAccount_WhenGitHubSelected_ShouldBeTrue()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.SelectedAccountType = "GitHub Codespaces";

        // Assert
        vm.IsGitHubAccount.Should().BeTrue();
        vm.IsLocalAccount.Should().BeFalse();
    }

    [Fact]
    public void IsLocalAccount_WhenLocalFolderSelected_ShouldBeTrue()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.SelectedAccountType = "Local Folder";

        // Assert
        vm.IsLocalAccount.Should().BeTrue();
        vm.IsGitHubAccount.Should().BeFalse();
    }

    [Fact]
    public void OnSelectedAccountTypeChanged_ShouldRaisePropertyChangedForBothComputedProperties()
    {
        // Arrange
        var vm = CreateViewModel();
        var changedProperties = new List<string>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

        // Act
        vm.SelectedAccountType = "GitHub Codespaces";

        // Assert
        changedProperties.Should().Contain(nameof(AddProfileViewModel.IsGitHubAccount));
        changedProperties.Should().Contain(nameof(AddProfileViewModel.IsLocalAccount));
    }

    #endregion

    #region SaveAsync Validation Tests

    [Fact]
    public async Task SaveCommand_WhenProfileNameEmpty_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "";

        // Act
        await vm.SaveCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Be("Please enter a profile name");
        await ProfileService.DidNotReceive().SaveProfileAsync(Arg.Any<Profile>());
    }

    [Fact]
    public async Task SaveCommand_WhenProfileNameWhitespace_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "   ";

        // Act
        await vm.SaveCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Be("Please enter a profile name");
    }

    [Fact]
    public async Task SaveCommand_WhenGitHubAccountWithoutUsername_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.SelectedAccountType = "GitHub Codespaces";
        vm.GitHubUsername = "";
        vm.GitHubToken = "token123";

        // Act
        await vm.SaveCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Be("Please enter your GitHub username");
    }

    [Fact]
    public async Task SaveCommand_WhenGitHubAccountWithoutToken_ShouldSetErrorMessage()
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
        vm.ErrorMessage.Should().Be("Please enter your GitHub token");
    }

    [Fact]
    public async Task SaveCommand_WhenLocalAccountWithoutPath_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.SelectedAccountType = "Local Folder";
        vm.LocalPath = "";

        // Act
        await vm.SaveCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Be("Please select a folder path");
    }

    [Fact]
    public async Task SaveCommand_WhenLocalAccountWithNonExistentPath_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.SelectedAccountType = "Local Folder";
        vm.LocalPath = "/this/path/does/not/exist/12345";

        // Act
        await vm.SaveCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Be("The selected folder does not exist");
    }

    #endregion

    #region SaveAsync Success Tests

    [Fact]
    public async Task SaveCommand_WhenValidGitHubAccount_ShouldSaveProfile()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.SelectedAccountType = "GitHub Codespaces";
        vm.GitHubUsername = "testuser";
        vm.GitHubToken = "token123";

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
        savedProfile!.Name.Should().Be("TestProfile");
        savedProfile.Accounts.Should().HaveCount(1);
        savedProfile.Accounts[0].Type.Should().Be("github");
        savedProfile.Accounts[0].Username.Should().Be("testuser");
        savedProfile.Accounts[0].Token.Should().Be("token123");
    }

    [Fact]
    public async Task SaveCommand_WhenValidLocalAccount_ShouldSaveProfile()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.SelectedAccountType = "Local Folder";
        vm.LocalPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); // Use existing path
        vm.LocalDisplayName = "My Projects";

        Profile? savedProfile = null;
        ProfileService.SaveProfileAsync(Arg.Do<Profile>(p => savedProfile = p))
            .Returns(Task.CompletedTask);

        // Act
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
        savedProfile!.Name.Should().Be("TestProfile");
        savedProfile.Accounts.Should().HaveCount(1);
        savedProfile.Accounts[0].Type.Should().Be("local");
        savedProfile.Accounts[0].Path.Should().Be(vm.LocalPath);
        savedProfile.Accounts[0].Metadata.Should().ContainKey("name");
        savedProfile.Accounts[0].Metadata!["name"].Should().Be("My Projects");
    }

    [Fact]
    public async Task SaveCommand_WhenValidLocalAccountWithoutDisplayName_ShouldSaveWithNullMetadata()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";
        vm.SelectedAccountType = "Local Folder";
        vm.LocalPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        vm.LocalDisplayName = ""; // Empty display name

        Profile? savedProfile = null;
        ProfileService.SaveProfileAsync(Arg.Do<Profile>(p => savedProfile = p))
            .Returns(Task.CompletedTask);

        // Act
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
        savedProfile!.Accounts[0].Metadata.Should().BeNull();
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
        vm.GitHubToken = "token123";

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
        vm.GitHubToken = "token123";

        var savingStates = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AddProfileViewModel.IsSaving))
                savingStates.Add(vm.IsSaving);
        };

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
