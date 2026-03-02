using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GodMode.Avalonia.ViewModels;

public partial class EditServerViewModel : ViewModelBase
{
	private readonly IProfileService _profileService;
	private readonly IDialogService _dialogService;
	private Account? _originalAccount;

	public event Action? Completed;

	[ObservableProperty]
	private string _profileName = string.Empty;

	[ObservableProperty]
	private int _accountIndex;

	[ObservableProperty]
	private string _selectedServerType = "Local Server";

	[ObservableProperty]
	private string _gitHubUsername = string.Empty;

	[ObservableProperty]
	private string _gitHubToken = string.Empty;

	[ObservableProperty]
	private string _serverUrl = string.Empty;

	[ObservableProperty]
	private string _serverDisplayName = string.Empty;

	[ObservableProperty]
	private bool _isLoading;

	[ObservableProperty]
	private bool _isSaving;

	[ObservableProperty]
	private string? _errorMessage;

	public bool WasDeleted { get; private set; }

	public bool IsGitHubCodespaces => SelectedServerType == "GitHub Codespaces";
	public bool IsLocalServer => SelectedServerType == "Local Server";

	public EditServerViewModel(
		INavigationService navigationService,
		IProfileService profileService,
		IDialogService dialogService)
		: base(navigationService)
	{
		_profileService = profileService;
		_dialogService = dialogService;
	}

	partial void OnProfileNameChanged(string value)
	{
		if (!string.IsNullOrEmpty(value))
			_ = LoadAccountAsync();
	}

	partial void OnAccountIndexChanged(int value)
	{
		if (!string.IsNullOrEmpty(ProfileName))
			_ = LoadAccountAsync();
	}

	private async Task LoadAccountAsync()
	{
		if (string.IsNullOrEmpty(ProfileName) || AccountIndex < 0)
			return;

		IsLoading = true;
		ErrorMessage = null;

		try
		{
			var profile = await _profileService.GetProfileAsync(ProfileName);
			if (profile == null) { ErrorMessage = "Profile not found"; return; }
			if (AccountIndex >= profile.Accounts.Count) { ErrorMessage = "Server not found"; return; }

			_originalAccount = profile.Accounts[AccountIndex];

			if (_originalAccount.Type == "github")
			{
				SelectedServerType = "GitHub Codespaces";
				GitHubUsername = _originalAccount.Username ?? string.Empty;
				GitHubToken = string.IsNullOrEmpty(_originalAccount.Token) ? string.Empty : "********";
			}
			else
			{
				SelectedServerType = "Local Server";
				ServerUrl = _originalAccount.Path ?? "http://localhost:31337";
				ServerDisplayName = _originalAccount.Metadata?.GetValueOrDefault("name") ?? string.Empty;
			}

			OnPropertyChanged(nameof(IsGitHubCodespaces));
			OnPropertyChanged(nameof(IsLocalServer));
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error loading server: {ex.Message}";
		}
		finally
		{
			IsLoading = false;
		}
	}

	[RelayCommand]
	private async Task SaveAsync()
	{
		ErrorMessage = null;

		if (string.IsNullOrWhiteSpace(ProfileName)) { ErrorMessage = "No profile selected"; return; }

		if (IsGitHubCodespaces)
		{
			if (string.IsNullOrWhiteSpace(GitHubUsername)) { ErrorMessage = "Please enter your GitHub username"; return; }
			if (string.IsNullOrWhiteSpace(GitHubToken)) { ErrorMessage = "Please enter your GitHub personal access token"; return; }
		}
		else if (IsLocalServer)
		{
			if (string.IsNullOrWhiteSpace(ServerUrl)) { ErrorMessage = "Please enter a server URL"; return; }
			if (!ServerUrl.StartsWith("http://") && !ServerUrl.StartsWith("https://"))
			{ ErrorMessage = "Server URL must start with http:// or https://"; return; }
		}

		IsSaving = true;

		try
		{
			var profile = await _profileService.GetProfileAsync(ProfileName);
			if (profile == null) { ErrorMessage = "Profile not found"; return; }
			if (AccountIndex >= profile.Accounts.Count) { ErrorMessage = "Server not found"; return; }

			var account = profile.Accounts[AccountIndex];

			if (IsGitHubCodespaces)
			{
				account.Type = "github";
				account.Username = GitHubUsername;
				if (GitHubToken != "********")
					account.Token = GitHubToken;
				account.Path = null;
				account.Metadata = null;
			}
			else
			{
				account.Type = "local";
				account.Path = ServerUrl;
				account.Username = null;
				account.Token = null;
				account.Metadata = !string.IsNullOrWhiteSpace(ServerDisplayName)
					? new Dictionary<string, string> { ["name"] = ServerDisplayName }
					: null;
			}

			if (profile.HasDuplicateAccount(account, AccountIndex))
			{
				ErrorMessage = IsGitHubCodespaces
					? "A GitHub account with this username already exists in the profile"
					: "A server with this URL already exists in the profile";
				return;
			}

			await _profileService.SaveProfileAsync(profile);
			Completed?.Invoke();
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error saving server: {ex.Message}";
		}
		finally
		{
			IsSaving = false;
		}
	}

	[RelayCommand]
	private async Task DeleteAsync()
	{
		ErrorMessage = null;

		var confirmed = await _dialogService.ConfirmAsync(
			"Delete Server",
			"Are you sure you want to remove this server from the profile?",
			"Delete",
			"Cancel");

		if (!confirmed) return;

		IsSaving = true;

		try
		{
			var profile = await _profileService.GetProfileAsync(ProfileName);
			if (profile == null) { ErrorMessage = "Profile not found"; return; }
			if (AccountIndex >= profile.Accounts.Count) { ErrorMessage = "Server not found"; return; }

			profile.Accounts.RemoveAt(AccountIndex);
			await _profileService.SaveProfileAsync(profile);
			WasDeleted = true;
			Completed?.Invoke();
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error deleting server: {ex.Message}";
		}
		finally
		{
			IsSaving = false;
		}
	}

	[RelayCommand]
	private void Cancel() => Completed?.Invoke();
}
