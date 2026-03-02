using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.ClientBase.Services.Models;

namespace GodMode.Avalonia.ViewModels;

public partial class EditServerViewModel : ViewModelBase
{
	private readonly IServerRegistryService _serverRegistry;
	private readonly IDialogService _dialogService;
	private ServerRegistration? _originalServer;

	public event Action? Completed;

	[ObservableProperty]
	private int _serverIndex;

	// Kept for backward compat — ignored internally
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
		IServerRegistryService serverRegistry,
		IDialogService dialogService)
		: base(navigationService)
	{
		_serverRegistry = serverRegistry;
		_dialogService = dialogService;
	}

	partial void OnServerIndexChanged(int value)
	{
		_ = LoadServerAsync();
	}

	// Backward compat: AccountIndex triggers load too
	partial void OnAccountIndexChanged(int value)
	{
		ServerIndex = value;
	}

	private async Task LoadServerAsync()
	{
		if (ServerIndex < 0)
			return;

		IsLoading = true;
		ErrorMessage = null;

		try
		{
			var servers = await _serverRegistry.GetServersAsync();
			if (ServerIndex >= servers.Count) { ErrorMessage = "Server not found"; return; }

			_originalServer = servers[ServerIndex];

			if (_originalServer.Type == "github")
			{
				SelectedServerType = "GitHub Codespaces";
				GitHubUsername = _originalServer.Username ?? string.Empty;
				GitHubToken = string.IsNullOrEmpty(_originalServer.Token) ? string.Empty : "********";
			}
			else
			{
				SelectedServerType = "Local Server";
				ServerUrl = _originalServer.Url ?? "http://localhost:31337";
				ServerDisplayName = _originalServer.DisplayName ?? string.Empty;
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
			var server = IsGitHubCodespaces
				? new ServerRegistration
				{
					Type = "github",
					Username = GitHubUsername,
					Token = GitHubToken != "********" ? GitHubToken : _originalServer?.Token
				}
				: new ServerRegistration
				{
					Type = "local",
					Url = ServerUrl,
					DisplayName = !string.IsNullOrWhiteSpace(ServerDisplayName) ? ServerDisplayName : null
				};

			if (_serverRegistry.IsDuplicate(server, ServerIndex))
			{
				ErrorMessage = IsGitHubCodespaces
					? "A GitHub account with this username already exists"
					: "A server with this URL already exists";
				return;
			}

			await _serverRegistry.UpdateServerAsync(ServerIndex, server);
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
			"Are you sure you want to remove this server?",
			"Delete",
			"Cancel");

		if (!confirmed) return;

		IsSaving = true;

		try
		{
			await _serverRegistry.RemoveServerAsync(ServerIndex);
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
