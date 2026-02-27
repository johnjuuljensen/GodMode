using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GodMode.Avalonia.ViewModels;

public partial class AddServerViewModel : ViewModelBase
{
	private readonly IProfileService _profileService;

	[ObservableProperty]
	private string _profileName = string.Empty;

	[ObservableProperty]
	private string[] _serverTypes = ["GitHub Codespaces", "Local Server"];

	[ObservableProperty]
	private string _selectedServerType = "Local Server";

	[ObservableProperty]
	private string _gitHubUsername = string.Empty;

	[ObservableProperty]
	private string _gitHubToken = string.Empty;

	[ObservableProperty]
	private string _serverUrl = "http://localhost:31337";

	[ObservableProperty]
	private string _serverDisplayName = string.Empty;

	[ObservableProperty]
	private bool _isSaving;

	[ObservableProperty]
	private string? _errorMessage;

	public bool IsGitHubCodespaces => SelectedServerType == "GitHub Codespaces";
	public bool IsLocalServer => SelectedServerType == "Local Server";

	public AddServerViewModel(INavigationService navigationService, IProfileService profileService)
		: base(navigationService)
	{
		_profileService = profileService;
	}

	partial void OnSelectedServerTypeChanged(string value)
	{
		OnPropertyChanged(nameof(IsGitHubCodespaces));
		OnPropertyChanged(nameof(IsLocalServer));
	}

	[RelayCommand]
	private async Task SaveAsync()
	{
		ErrorMessage = null;

		if (string.IsNullOrWhiteSpace(ProfileName))
		{
			ErrorMessage = "No profile selected";
			return;
		}

		if (IsGitHubCodespaces)
		{
			if (string.IsNullOrWhiteSpace(GitHubUsername))
			{
				ErrorMessage = "Please enter your GitHub username";
				return;
			}
			if (string.IsNullOrWhiteSpace(GitHubToken))
			{
				ErrorMessage = "Please enter your GitHub personal access token";
				return;
			}
		}
		else if (IsLocalServer)
		{
			if (string.IsNullOrWhiteSpace(ServerUrl))
			{
				ErrorMessage = "Please enter a server URL";
				return;
			}
			if (!ServerUrl.StartsWith("http://") && !ServerUrl.StartsWith("https://"))
			{
				ErrorMessage = "Server URL must start with http:// or https://";
				return;
			}
		}

		IsSaving = true;

		try
		{
			var profile = await _profileService.GetProfileAsync(ProfileName);
			if (profile == null)
			{
				ErrorMessage = "Profile not found";
				return;
			}

			var account = IsGitHubCodespaces
				? new Account
				{
					Type = "github",
					Username = GitHubUsername,
					Token = GitHubToken
				}
				: new Account
				{
					Type = "local",
					Path = ServerUrl,
					Metadata = !string.IsNullOrWhiteSpace(ServerDisplayName)
						? new Dictionary<string, string> { ["name"] = ServerDisplayName }
						: null
				};

			profile.Accounts.Add(account);
			await _profileService.SaveProfileAsync(profile);
			Navigation.GoBack();
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error adding server: {ex.Message}";
		}
		finally
		{
			IsSaving = false;
		}
	}

	[RelayCommand]
	private void Cancel() => Navigation.GoBack();
}
