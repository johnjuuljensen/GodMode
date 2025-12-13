using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using GodMode.Maui.ViewModels;

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
        vm.IsSaving.Should().BeFalse();
        vm.ErrorMessage.Should().BeNull();
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
    public async Task SaveCommand_WhenProfileAlreadyExists_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "ExistingProfile";

        ProfileService.GetProfileAsync("ExistingProfile")
            .Returns(Task.FromResult<Profile?>(new Profile { Name = "ExistingProfile", Accounts = [] }));

        // Act
        await vm.SaveCommand.ExecuteAsync(null);

        // Assert
        vm.ErrorMessage.Should().Be("A profile with this name already exists");
        await ProfileService.DidNotReceive().SaveProfileAsync(Arg.Any<Profile>());
    }

    #endregion

    #region SaveAsync Success Tests

    [Fact]
    public async Task SaveCommand_WhenValidProfileName_ShouldSaveProfileWithEmptyAccounts()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "NewProfile";

        ProfileService.GetProfileAsync("NewProfile").Returns(Task.FromResult<Profile?>(null));

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
        savedProfile!.Name.Should().Be("NewProfile");
        savedProfile.Accounts.Should().BeEmpty();
    }

    #endregion

    #region SaveAsync Error Handling Tests

    [Fact]
    public async Task SaveCommand_WhenExceptionThrown_ShouldSetErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ProfileName = "TestProfile";

        ProfileService.GetProfileAsync("TestProfile").Returns(Task.FromResult<Profile?>(null));
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

        var savingStates = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AddProfileViewModel.IsSaving))
                savingStates.Add(vm.IsSaving);
        };

        ProfileService.GetProfileAsync("TestProfile").Returns(Task.FromResult<Profile?>(null));
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
