using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.ClientBase.Services.Models;

namespace GodMode.Avalonia.ViewModels;

public partial class SystemMappingItem : ObservableObject
{
	[ObservableProperty]
	private string _systemName = string.Empty;

	[ObservableProperty]
	private string _credentialName = string.Empty;
}

public partial class EditServerViewModel : ViewModelBase
{
	private readonly IProfileService _profileService;
	private readonly IDialogService _dialogService;
	private readonly ICredentialService _credentialService;
	private ServerConfig? _originalServer;

	public event Action? Completed;

	[ObservableProperty]
	private string _profileName = string.Empty;

	[ObservableProperty]
	private string _serverId = string.Empty;

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

	[ObservableProperty]
	private string _newSystemName = string.Empty;

	[ObservableProperty]
	private string _newCredentialName = string.Empty;

	public ObservableCollection<SystemMappingItem> SystemMappings { get; } = new();
	public ObservableCollection<string> AvailableCredentials { get; } = new();

	public bool IsGitHubCodespaces => SelectedServerType == "GitHub Codespaces";
	public bool IsLocalServer => SelectedServerType == "Local Server";

	public EditServerViewModel(
		INavigationService navigationService,
		IProfileService profileService,
		IDialogService dialogService,
		ICredentialService credentialService)
		: base(navigationService)
	{
		_profileService = profileService;
		_dialogService = dialogService;
		_credentialService = credentialService;
	}

	partial void OnProfileNameChanged(string value)
	{
		if (!string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(ServerId))
			_ = LoadServerAsync();
	}

	partial void OnServerIdChanged(string value)
	{
		if (!string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(ProfileName))
			_ = LoadServerAsync();
	}

	private async Task LoadServerAsync()
	{
		if (string.IsNullOrEmpty(ProfileName) || string.IsNullOrEmpty(ServerId))
			return;

		IsLoading = true;
		ErrorMessage = null;

		try
		{
			var profile = await _profileService.GetProfileAsync(ProfileName);
			if (profile == null) { ErrorMessage = "Profile not found"; return; }

			_originalServer = profile.Servers.FirstOrDefault(s => s.Id == ServerId);
			if (_originalServer == null) { ErrorMessage = "Server not found"; return; }

			if (_originalServer.Type == "github")
			{
				SelectedServerType = "GitHub Codespaces";
				GitHubUsername = _originalServer.Username ?? string.Empty;
				GitHubToken = string.IsNullOrEmpty(_originalServer.Token) ? string.Empty : "********";
			}
			else
			{
				SelectedServerType = "Local Server";
				ServerUrl = _originalServer.Path ?? "http://localhost:31337";
				ServerDisplayName = _originalServer.DisplayName
					?? _originalServer.Metadata?.GetValueOrDefault("name")
					?? string.Empty;
			}

			OnPropertyChanged(nameof(IsGitHubCodespaces));
			OnPropertyChanged(nameof(IsLocalServer));

			// Load system mappings
			SystemMappings.Clear();
			if (_originalServer.Systems is { Count: > 0 })
			{
				foreach (var (system, credential) in _originalServer.Systems)
					SystemMappings.Add(new SystemMappingItem { SystemName = system, CredentialName = credential });
			}

			// Load available credentials
			await LoadCredentialsAsync();
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

	private async Task LoadCredentialsAsync()
	{
		AvailableCredentials.Clear();
		try
		{
			var credentials = await _credentialService.ListCredentialsAsync(ProfileName);
			foreach (var c in credentials)
				AvailableCredentials.Add(c.Name);
		}
		catch
		{
			// Non-critical — user can still type credential names
		}
	}

	[RelayCommand]
	private void AddSystemMapping()
	{
		if (string.IsNullOrWhiteSpace(NewSystemName) || string.IsNullOrWhiteSpace(NewCredentialName))
			return;

		// Don't allow duplicate system names
		if (SystemMappings.Any(m => m.SystemName.Equals(NewSystemName, StringComparison.OrdinalIgnoreCase)))
		{
			ErrorMessage = $"System '{NewSystemName}' is already mapped";
			return;
		}

		SystemMappings.Add(new SystemMappingItem { SystemName = NewSystemName.Trim(), CredentialName = NewCredentialName.Trim() });
		NewSystemName = string.Empty;
		NewCredentialName = string.Empty;
		ErrorMessage = null;
	}

	[RelayCommand]
	private void RemoveSystemMapping(SystemMappingItem item)
	{
		SystemMappings.Remove(item);
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

			var server = profile.Servers.FirstOrDefault(s => s.Id == ServerId);
			if (server == null) { ErrorMessage = "Server not found"; return; }

			if (IsGitHubCodespaces)
			{
				server.Type = "github";
				server.Username = GitHubUsername;
				if (GitHubToken != "********")
					server.Token = GitHubToken;
				server.Path = null;
				server.DisplayName = null;
			}
			else
			{
				server.Type = "local";
				server.Path = ServerUrl;
				server.Username = null;
				server.Token = null;
				server.DisplayName = !string.IsNullOrWhiteSpace(ServerDisplayName) ? ServerDisplayName : null;
			}

			// Save system mappings
			server.Systems = SystemMappings.Count > 0
				? SystemMappings.ToDictionary(m => m.SystemName, m => m.CredentialName)
				: null;

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

			var server = profile.Servers.FirstOrDefault(s => s.Id == ServerId);
			if (server == null) { ErrorMessage = "Server not found"; return; }

			profile.Servers.Remove(server);
			await _profileService.SaveProfileAsync(profile);
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
