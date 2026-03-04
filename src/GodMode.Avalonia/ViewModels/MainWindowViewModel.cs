using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.Avalonia.Services;
using GodMode.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace GodMode.Avalonia.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
	private readonly IThemeService _themeService;
	private readonly INotificationService _notificationService;
	private readonly IHostConnectionService _hostConnectionService;
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

	public VoiceAssistantViewModel? Voice { get; }

	public bool IsVoiceSupported { get; }

	// Auto-restart banner
	[ObservableProperty]
	private bool _showRestartBanner;

	[ObservableProperty]
	private string? _restartBannerMessage;

	[ObservableProperty]
	private bool _restartBannerIsError;

	// Compact/mobile mode
	[ObservableProperty]
	private bool _isCompact;

	[ObservableProperty]
	private object? _compactContent;

	[ObservableProperty]
	private bool _canGoBack;

	private readonly Stack<object?> _compactNavStack = new();

	public MainWindowViewModel(
		MainViewModel mainViewModel,
		IThemeService themeService,
		INotificationService notificationService,
		IHostConnectionService hostConnectionService,
		VoiceAssistantViewModel? voiceAssistantViewModel = null)
	{
		_themeService = themeService;
		_notificationService = notificationService;
		_hostConnectionService = hostConnectionService;
		_sidebarViewModel = mainViewModel;
		Voice = voiceAssistantViewModel;
		IsVoiceSupported = Voice != null;

		mainViewModel.ProjectSelected += OnProjectSelected;
		mainViewModel.CreateProjectRequested += OnCreateProjectRequested;
		mainViewModel.AddServerRequested += OnAddServerRequested;
		mainViewModel.EditServerRequested += OnEditServerRequested;
		mainViewModel.ConnectionStateChanged += connected => IsConnected = connected;

		_notificationService.BadgeCountUpdated += (_, _) => UpdateWaitingBadge();

		// Compact mode starts showing the sidebar (project list)
		_compactContent = mainViewModel;

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
				vm.CreateProjectRequested += OnCreateProjectRequested;
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

	private void OnTileProjectSelected(RootGroupViewModel root, ProjectSummary project)
	{
		IsTileFullscreen = true;

		var key = $"{root.HostId}:{project.Id}";

		if (!_projectViewModels.TryGetValue(key, out var vm))
		{
			vm = App.Services.GetRequiredService<ProjectViewModel>();
			vm.ProfileName = root.ProfileName;
			vm.HostId = root.HostId;
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
			vm.CreateProjectRequested += OnCreateProjectRequested;
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

	// === Compact mode navigation ===

	[RelayCommand]
	private void GoBack()
	{
		if (_compactNavStack.Count > 0)
		{
			CompactContent = _compactNavStack.Pop();
			CanGoBack = _compactNavStack.Count > 0;
		}
	}

	[RelayCommand]
	private void NavigateToMenu()
	{
		if (!IsCompact) return;
		CompactNavigateTo(new MobileMenuViewModel(this));
	}

	[RelayCommand]
	private void NavigateToVoice()
	{
		if (!IsCompact || Voice == null) return;
		CompactNavigateTo(Voice);
	}

	private void CompactNavigateTo(object content)
	{
		_compactNavStack.Push(CompactContent);
		CompactContent = content;
		CanGoBack = true;
	}

	private void OnProjectStatusUpdated(string projectId, GodMode.Shared.Enums.ProjectState state, string? currentQuestion)
	{
		Dispatcher.UIThread.Post(() =>
		{
			foreach (var profileGroup in SidebarViewModel.ProfileGroups)
			{
				foreach (var rootGroup in profileGroup.RootGroups)
				{
					for (int i = 0; i < rootGroup.Projects.Count; i++)
					{
						if (rootGroup.Projects[i].Id == projectId)
						{
							var old = rootGroup.Projects[i];
							rootGroup.Projects[i] = old with { State = state, CurrentQuestion = currentQuestion, UpdatedAt = DateTime.UtcNow };

							if (SidebarViewModel.SelectedProject?.Id == projectId)
								SidebarViewModel.SelectedProject = rootGroup.Projects[i];

							break;
						}
					}
				}
			}

			// Also update servers list for tile view compat
			foreach (var server in SidebarViewModel.Servers)
			{
				for (int i = 0; i < server.Projects.Count; i++)
				{
					if (server.Projects[i].Id == projectId)
					{
						var old = server.Projects[i];
						server.Projects[i] = old with { State = state, CurrentQuestion = currentQuestion, UpdatedAt = DateTime.UtcNow };
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

	private void UpdateWaitingBadge()
	{
		var count = _notificationService.GetTotalBadgeCount();
		Dispatcher.UIThread.Post(() =>
		{
			WaitingBadgeCount = count;
			HasWaitingProjects = count > 0;
		});
	}

	private void OnProjectSelected(RootGroupViewModel root, ProjectSummary project)
	{
		var key = $"{root.HostId}:{project.Id}";

		if (!_projectViewModels.TryGetValue(key, out var vm))
		{
			vm = App.Services.GetRequiredService<ProjectViewModel>();
			vm.ProfileName = root.ProfileName;
			vm.HostId = root.HostId;
			vm.ProjectId = project.Id;
			vm.ProjectStatusUpdated += OnProjectStatusUpdated;
			_projectViewModels[key] = vm;
		}

		if (IsCompact)
		{
			CompactNavigateTo(vm);
		}
		else
		{
			ContentViewModel = vm;
		}
	}

	private void OnCreateProjectRequested(RootGroupViewModel root, string? actionName)
	{
		var vm = App.Services.GetRequiredService<CreateProjectViewModel>();
		vm.ProfileName = root.ProfileName;
		vm.HostId = root.HostId;
		vm.PreselectedRootName = root.RootName;
		vm.PreselectedActionName = actionName;

		var dismiss = ShowAsModalOrNavigate(vm);
		vm.Completed += dismiss;
	}

	private void OnAddServerRequested()
	{
		var vm = App.Services.GetRequiredService<AddServerViewModel>();
		var dismiss = ShowAsModalOrNavigate(vm);
		vm.Completed += () =>
		{
			dismiss();
			_ = SidebarViewModel.RefreshCommand.ExecuteAsync(null);
		};
	}

	private void OnEditServerRequested(ServerGroupViewModel server)
	{
		var vm = App.Services.GetRequiredService<EditServerViewModel>();
		vm.ServerIndex = server.AccountIndex;

		var dismiss = ShowAsModalOrNavigate(vm);
		vm.Completed += () =>
		{
			if (vm.WasDeleted)
				_hostConnectionService.DisconnectFromHost(server.Id);

			dismiss();
			_ = SidebarViewModel.RefreshCommand.ExecuteAsync(null);
		};
	}

	private Action ShowAsModalOrNavigate(object vm)
	{
		if (IsCompact)
		{
			CompactNavigateTo(vm);
			return GoBack;
		}

		ModalViewModel = vm;
		IsModalVisible = true;
		return CloseModal;
	}

	[RelayCommand]
	private void CloseModal()
	{
		IsModalVisible = false;
		ModalViewModel = null;
	}
}
