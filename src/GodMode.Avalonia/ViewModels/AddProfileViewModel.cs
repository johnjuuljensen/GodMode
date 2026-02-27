using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GodMode.Avalonia.ViewModels;

public partial class AddProfileViewModel : ViewModelBase
{
	private readonly IProfileService _profileService;

	[ObservableProperty]
	private string _profileName = string.Empty;

	[ObservableProperty]
	private bool _isSaving;

	[ObservableProperty]
	private string? _errorMessage;

	public AddProfileViewModel(INavigationService navigationService, IProfileService profileService)
		: base(navigationService)
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

		var existingProfile = await _profileService.GetProfileAsync(ProfileName);
		if (existingProfile != null)
		{
			ErrorMessage = "A profile with this name already exists";
			return;
		}

		IsSaving = true;

		try
		{
			var profile = new Profile { Name = ProfileName, Accounts = [] };
			await _profileService.SaveProfileAsync(profile);
			Navigation.GoBack();
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
	private void Cancel() => Navigation.GoBack();
}
