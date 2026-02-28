using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.Avalonia.Services;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace GodMode.Avalonia.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
	private readonly IThemeService _themeService;
	private readonly INotificationService _notificationService;
	private readonly Dictionary<string, ProjectViewModel> _projectViewModels = new();

	[ObservableProperty]
	private MainViewModel _sidebarViewModel;

	[ObservableProperty]
	private object? _contentViewModel;

	[ObservableProperty]
	private object? _modalViewModel;

	[ObservableProperty]
	private bool _isModalVisible;

	[ObservableProperty]
	private bool _isConnected;

	[ObservableProperty]
	private bool _isDarkTheme = true;

	[ObservableProperty]
	private string _themeIcon = "☾";

	[ObservableProperty]
	private int _waitingBadgeCount;

	[ObservableProperty]
	private bool _hasWaitingProjects;

	[ObservableProperty]
	private bool _isTileView;

	[ObservableProperty]
	private bool _isTileFullscreen;

	[ObservableProperty]
	private string _viewModeIcon = "☰";

	// Voice panel
	[ObservableProperty]
	private bool _isVoicePanelOpen;

#if VOICE_ENABLED
	public VoiceAssistantViewModel? Voice { get; }
#else
	public object? Voice => null;
#endif

	public bool IsVoiceSupported { get; }

	// Auto-restart banner
	[ObservableProperty]
	private bool _showRestartBanner;

	[ObservableProperty]
	private string? _restartBannerMessage;

	[ObservableProperty]
	private bool _restartBannerIsError;

	public MainWindowViewModel(
		MainViewModel mainViewModel,
		IThemeService themeService,
		INotificationService notificationService
#if VOICE_ENABLED
		, VoiceAssistantViewModel? voiceAssistantViewModel = null
#endif
		)
	{
		_themeService = themeService;
		_notificationService = notificationService;
		_sidebarViewModel = mainViewModel;
#if VOICE_ENABLED
		Voice = voiceAssistantViewModel;
		IsVoiceSupported = Voice != null;
#endif

		mainViewModel.ProjectSelected += OnProjectSelected;
		mainViewModel.CreateProjectRequested += OnCreateProjectRequested;
		mainViewModel.AddServerRequested += OnAddServerRequested;
		mainViewModel.AddProfileRequested += OnAddProfileRequested;
		mainViewModel.EditServerRequested += OnEditServerRequested;
		mainViewModel.ConnectionStateChanged += connected => IsConnected = connected;
		mainViewModel.StatusChanged += OnServerStatusChanged;

		_notificationService.BadgeCountUpdated += (_, _) => UpdateWaitingBadge();

		_ = mainViewModel.LoadCommand.ExecuteAsync(null);
	}

	[RelayCommand]
	private void ToggleVoicePanel() => IsVoicePanelOpen = !IsVoicePanelOpen;

	[RelayCommand]
	private void ToggleTheme()
	{
		_themeService.ToggleTheme();
		IsDarkTheme = _themeService.IsDark;
		ThemeIcon = IsDarkTheme ? "☾" : "☀";
	}

	[RelayCommand]
	private void ToggleViewMode()
	{
		IsTileView = !IsTileView;
		IsTileFullscreen = false;
		ViewModeIcon = IsTileView ? "⊞" : "☰";

		if (IsTileView)
		{
			_savedContentViewModel = ContentViewModel;

			if (_tileGridViewModel != null)
			{
				ContentViewModel = _tileGridViewModel;
			}
			else
			{
				var vm = App.Services.GetRequiredService<TileGridViewModel>();
				vm.ProjectSelected += OnTileProjectSelected;
				_tileGridViewModel = vm;
				ContentViewModel = vm;
				_ = vm.LoadAsync(SidebarViewModel.Servers);
			}
		}
		else
		{
			ContentViewModel = _savedContentViewModel;
			_savedContentViewModel = null;
		}
	}

	private object? _savedContentViewModel;
	private TileGridViewModel? _tileGridViewModel;

	private void OnTileProjectSelected(ServerGroupViewModel server, ProjectSummary project)
	{
		IsTileFullscreen = true;

		var key = $"{server.ProfileName}:{server.Id}:{project.Id}";

		if (!_projectViewModels.TryGetValue(key, out var vm))
		{
			vm = App.Services.GetRequiredService<ProjectViewModel>();
			vm.ProfileName = server.ProfileName;
			vm.HostId = server.Id;
			vm.ProjectId = project.Id;
			vm.ProjectStatusUpdated += OnProjectStatusUpdated;
			_projectViewModels[key] = vm;
		}

		ContentViewModel = vm;
	}

	[RelayCommand]
	private void BackToTiles()
	{
		IsTileFullscreen = false;

		if (_tileGridViewModel != null)
		{
			ContentViewModel = _tileGridViewModel;
		}
		else
		{
			var vm = App.Services.GetRequiredService<TileGridViewModel>();
			vm.ProjectSelected += OnTileProjectSelected;
			_tileGridViewModel = vm;
			ContentViewModel = vm;
			_ = vm.LoadAsync(SidebarViewModel.Servers);
		}
	}

	[RelayCommand]
	private void DismissRestartBanner()
	{
		ShowRestartBanner = false;
		RestartBannerMessage = null;
	}

	public void ShowAutoRestartBanner(string projectName, bool success)
	{
		RestartBannerIsError = !success;
		RestartBannerMessage = success
			? $"Auto-restarted: {projectName}"
			: $"Restart failed: {projectName} (too many restarts)";
		ShowRestartBanner = true;

		_ = Task.Run(async () =>
		{
			await Task.Delay(10_000);
			Dispatcher.UIThread.Post(() =>
			{
				if (RestartBannerMessage?.Contains(projectName) == true)
					DismissRestartBanner();
			});
		});
	}

	private void OnProjectStatusUpdated(string projectId, GodMode.Shared.Enums.ProjectState state, string? currentQuestion)
	{
		Dispatcher.UIThread.Post(() =>
		{
			foreach (var server in SidebarViewModel.Servers)
			{
				for (int i = 0; i < server.Projects.Count; i++)
				{
					if (server.Projects[i].Id == projectId)
					{
						var old = server.Projects[i];
						server.Projects[i] = old with { State = state, CurrentQuestion = currentQuestion, UpdatedAt = DateTime.UtcNow };

						if (SidebarViewModel.SelectedProject?.Id == projectId)
							SidebarViewModel.SelectedProject = server.Projects[i];

						break;
					}
				}
			}

			if (_tileGridViewModel != null)
			{
				foreach (var tile in _tileGridViewModel.Tiles)
				{
					if (tile.Summary.Id == projectId)
					{
						tile.Summary = tile.Summary with { State = state, CurrentQuestion = currentQuestion, UpdatedAt = DateTime.UtcNow };
						break;
					}
				}
			}
		});
	}

	private void OnServerStatusChanged(ServerGroupViewModel server, string projectId, ProjectStatus status)
	{
		Dispatcher.UIThread.Post(() =>
		{
			if (_tileGridViewModel != null)
			{
				foreach (var tile in _tileGridViewModel.Tiles)
				{
					if (tile.Summary.Id == projectId)
					{
						tile.Summary = tile.Summary with
						{
							State = status.State,
							CurrentQuestion = status.CurrentQuestion,
							UpdatedAt = status.UpdatedAt
						};
						break;
					}
				}
			}

			if (status.State == ProjectState.WaitingInput)
			{
				_notificationService.NotifyProjectNeedsInput(
					server.ProfileName, server.Id, projectId,
					status.Name, status.CurrentQuestion);
			}
			else if (status.State is ProjectState.Running or ProjectState.Idle)
			{
				_notificationService.ClearBadgeCountForProject(
					server.ProfileName, server.Id, projectId);
			}

			UpdateWaitingBadge();
		});
	}

	private void UpdateWaitingBadge()
	{
		var count = _notificationService.GetTotalBadgeCount();
		Dispatcher.UIThread.Post(() =>
		{
			WaitingBadgeCount = count;
			HasWaitingProjects = count > 0;
		});
	}

	private void OnProjectSelected(ServerGroupViewModel server, ProjectSummary project)
	{
		var key = $"{server.ProfileName}:{server.Id}:{project.Id}";

		if (!_projectViewModels.TryGetValue(key, out var vm))
		{
			vm = App.Services.GetRequiredService<ProjectViewModel>();
			vm.ProfileName = server.ProfileName;
			vm.HostId = server.Id;
			vm.ProjectId = project.Id;
			vm.ProjectStatusUpdated += OnProjectStatusUpdated;
			_projectViewModels[key] = vm;
		}

		ContentViewModel = vm;
	}

	private void OnCreateProjectRequested(ServerGroupViewModel server)
	{
		var vm = App.Services.GetRequiredService<CreateProjectViewModel>();
		vm.ProfileName = server.ProfileName;
		vm.HostId = server.Id;
		vm.ServerId = server.ServerId;
		vm.ServerName = server.Name;
		vm.Completed += () => CloseModal();
		ModalViewModel = vm;
		IsModalVisible = true;
	}

	private void OnAddServerRequested(string profileName)
	{
		var vm = App.Services.GetRequiredService<AddServerViewModel>();
		vm.ProfileName = profileName;
		vm.Completed += () =>
		{
			CloseModal();
			_ = SidebarViewModel.RefreshCommand.ExecuteAsync(null);
		};
		ModalViewModel = vm;
		IsModalVisible = true;
	}

	private void OnAddProfileRequested()
	{
		var vm = App.Services.GetRequiredService<AddProfileViewModel>();
		vm.Completed += () =>
		{
			CloseModal();
			_ = SidebarViewModel.LoadCommand.ExecuteAsync(null);
		};
		ModalViewModel = vm;
		IsModalVisible = true;
	}

	private void OnEditServerRequested(ServerGroupViewModel server)
	{
		var vm = App.Services.GetRequiredService<EditServerViewModel>();
		vm.ProfileName = server.ProfileName;
		vm.ServerId = server.ServerId;
		vm.Completed += () =>
		{
			CloseModal();
			_ = SidebarViewModel.RefreshCommand.ExecuteAsync(null);
		};
		ModalViewModel = vm;
		IsModalVisible = true;
	}

	[RelayCommand]
	private void ManageCredentials()
	{
		var vm = App.Services.GetRequiredService<CredentialListViewModel>();

		if (SidebarViewModel.IsAllProfilesSelected)
		{
			vm.IsAllProfiles = true;
			vm.AllProfileNames = SidebarViewModel.ProfileOptions
				.Where(p => p != MainViewModel.AllProfilesOption)
				.Select(p => p.Name)
				.ToList();
		}
		else
		{
			vm.ProfileName = SidebarViewModel.SelectedProfile!.Name;
		}

		vm.Completed += () => CloseModal();
		_ = vm.LoadCommand.ExecuteAsync(null);
		ModalViewModel = vm;
		IsModalVisible = true;
	}

	[RelayCommand]
	private void CloseModal()
	{
		IsModalVisible = false;
		ModalViewModel = null;
	}
}
