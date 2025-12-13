using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GodMode.Maui.ViewModels;

/// <summary>
/// ViewModel for adding a new profile (name only)
/// </summary>
public partial class AddProfileViewModel : ObservableObject
{
    private readonly IProfileService _profileService;

    [ObservableProperty]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private string? _errorMessage;

    public AddProfileViewModel(IProfileService profileService)
    {
        _profileService = profileService;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = null;

        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            ErrorMessage = "Please enter a profile name";
            return;
        }

        // Check if profile already exists
        var existingProfile = await _profileService.GetProfileAsync(ProfileName);
        if (existingProfile != null)
        {
            ErrorMessage = "A profile with this name already exists";
            return;
        }

        IsSaving = true;

        try
        {
            var profile = new Services.Models.Profile
            {
                Name = ProfileName,
                Accounts = []
            };

            await _profileService.SaveProfileAsync(profile);
            await Shell.Current!.GoToAsync("..");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error saving profile: {ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        await Shell.Current!.GoToAsync("..");
    }
}
