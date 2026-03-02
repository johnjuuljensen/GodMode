using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.ClientBase.Services.Models;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using System.Collections.ObjectModel;

using Profile = GodMode.ClientBase.Services.Models.Profile;

namespace GodMode.Avalonia.ViewModels;

public partial class MainViewModel : ViewModelBase
{
	private readonly IProfileService _profileService;
	private readonly IHostConnectionService _hostConnectionService;
	private readonly IProjectService _projectService;
	private readonly INotificationService _notificationService;
	private readonly IDialogService _dialogService;
	private readonly HashSet<string> _subscribedConnections = new();
	private readonly HashSet<string> _hiddenProjectIds = new();
	private bool _suppressProfileChange;

	// Events for shell orchestration (replaces page navigation)
	public event Action<ServerGroupViewModel, ProjectSummary>? ProjectSelected;
	public event Action<ServerGroupViewModel, string?, string?>? CreateProjectRequested;
	public event Action<string>? AddServerRequested;
	public event Action? AddProfileRequested;
	public event Action<ServerGroupViewModel>? EditServerRequested;
	public event Action<bool>? ConnectionStateChanged;

	public static readonly Profile AllProfilesOption = new() { Name = "All" };

	[ObservableProperty]
	private ObservableCollection<Profile> _profileOptions = new();

	[ObservableProperty]
	private Profile? _selectedProfileOption;

	[ObservableProperty]
	private ObservableCollection<ServerGroupViewModel> _servers = new();

	[ObservableProperty]
	private bool _isLoading;

	[ObservableProperty]
	private string? _errorMessage;

	[ObservableProperty]
	private ProjectSummary? _selectedProject;

	[ObservableProperty]
	private bool _showHiddenProjects;

	[ObservableProperty]
	private int _hiddenProjectCount;

	[ObservableProperty]
	private bool _isTileView;

	[ObservableProperty]
	private bool _isGroupedByServer = true;

	[ObservableProperty]
	private bool _sortByName;

	public MainViewModel(
		INavigationService navigationService,
		IProfileService profileService,
		IHostConnectionService hostConnectionService,
		IProjectService projectService,
		INotificationService notificationService,
		IDialogService dialogService)
		: base(navigationService)
	{
		_profileService = profileService;
		_hostConnectionService = hostConnectionService;
		_projectService = projectService;
		_notificationService = notificationService;
		_dialogService = dialogService;
	}

	public bool IsAllProfilesSelected => SelectedProfileOption == AllProfilesOption;
	public Profile? SelectedProfile => IsAllProfilesSelected ? null : SelectedProfileOption;

	[RelayCommand]
	private async Task LoadAsync()
	{
		IsLoading = true;
		ErrorMessage = null;

		try
		{
			System.Diagnostics.Debug.WriteLine("[GodMode] LoadAsync started");
			var profiles = await _profileService.GetProfilesAsync();
			System.Diagnostics.Debug.WriteLine($"[GodMode] Loaded {profiles.Count()} profiles");
			var options = new List<Profile> { AllProfilesOption };
			options.AddRange(profiles);
			ProfileOptions = new ObservableCollection<Profile>(options);

			var selectedName = await _profileService.GetSelectedProfileAsync();
			// Suppress the OnSelectedProfileOptionChanged callback during initial load
			// to prevent a duplicate concurrent LoadServersAsync call
			_suppressProfileChange = true;
			SelectedProfileOption = selectedName != null
				? ProfileOptions.FirstOrDefault(p => p.Name == selectedName.Name) ?? AllProfilesOption
				: AllProfilesOption;
			_suppressProfileChange = false;
			System.Diagnostics.Debug.WriteLine($"[GodMode] Selected profile: {SelectedProfileOption?.Name}");

			await LoadServersAsync();
			System.Diagnostics.Debug.WriteLine($"[GodMode] After LoadServersAsync, Servers.Count={Servers.Count}");
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[GodMode] LoadAsync error: {ex}");
			ErrorMessage = $"Error loading profiles: {ex.Message}";
		}
		finally
		{
			IsLoading = false;
		}
	}

	[RelayCommand]
	private async Task RefreshAsync()
	{
		IsLoading = true;
		ErrorMessage = null;

		try
		{
			await LoadServersAsync();
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error refreshing: {ex.Message}";
		}
		finally
		{
			IsLoading = false;
		}
	}

	[RelayCommand]
	private async Task ToggleServerExpandedAsync(ServerGroupViewModel server)
	{
		server.IsExpanded = !server.IsExpanded;
		if (server.IsExpanded && server.Projects.Count == 0 && server.CanConnect)
			await LoadServerProjectsAsync(server);
	}

	[RelayCommand]
	private void SelectProject(ProjectSummary project)
	{
		var server = Servers.FirstOrDefault(s => s.Projects.Any(p => p.Id == project.Id));
		if (server == null) return;

		SelectedProject = project;
		ProjectSelected?.Invoke(server, project);
	}

	[RelayCommand]
	private void CreateProject(ServerGroupViewModel server)
	{
		CreateProjectRequested?.Invoke(server, null, null);
	}

	[RelayCommand]
	private void CreateProjectForRoot(RootGroupViewModel root)
	{
		CreateProjectRequested?.Invoke(root.Server, root.Name, null);
	}

	[RelayCommand]
	private void CreateProjectForRootAction(RootActionItem item)
	{
		CreateProjectRequested?.Invoke(item.Root.Server, item.Root.Name, item.Action.Name);
	}

	[RelayCommand]
	private void EditServer(ServerGroupViewModel server)
	{
		EditServerRequested?.Invoke(server);
	}

	[RelayCommand]
	private void AddProfile()
	{
		AddProfileRequested?.Invoke();
	}

	[RelayCommand]
	private void AddServer()
	{
		if (SelectedProfile != null)
		{
			AddServerRequested?.Invoke(SelectedProfile.Name);
		}
		else if (ProfileOptions.Count > 1)
		{
			var firstProfile = ProfileOptions.FirstOrDefault(p => p != AllProfilesOption);
			if (firstProfile != null)
				AddServerRequested?.Invoke(firstProfile.Name);
		}
		else
		{
			ErrorMessage = "Create a profile first before adding servers";
		}
	}

	[RelayCommand]
	private void HideProject(ProjectSummary project)
	{
		_hiddenProjectIds.Add(project.Id);
		HiddenProjectCount = _hiddenProjectIds.Count;

		// Remove from visible projects if not showing hidden
		if (!ShowHiddenProjects)
		{
			foreach (var server in Servers)
			{
				var found = server.Projects.FirstOrDefault(p => p.Id == project.Id);
				if (found != null)
				{
					server.Projects.Remove(found);
					break;
				}
			}
		}
	}

	[RelayCommand]
	private void UnhideProject(ProjectSummary project)
	{
		_hiddenProjectIds.Remove(project.Id);
		HiddenProjectCount = _hiddenProjectIds.Count;
	}

	[RelayCommand]
	private void ToggleShowHidden()
	{
		ShowHiddenProjects = !ShowHiddenProjects;
		_ = RefreshAsync();
	}

	[RelayCommand]
	private async Task DeleteProjectAsync(ProjectSummary project)
	{
		var server = Servers.FirstOrDefault(s => s.Projects.Contains(project));
		if (server == null) return;

		var (confirmed, force) = await _dialogService.ConfirmDeleteAsync(project.Name);
		if (!confirmed) return;

		try
		{
			await _projectService.DeleteProjectAsync(server.ProfileName, server.Id, project.Id, force);
			server.Projects.Remove(project);
			server.RebuildRootGroups(SortByName);
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error deleting project: {ex.Message}";
		}
	}

	[RelayCommand]
	private void ToggleGroupByServer()
	{
		IsGroupedByServer = !IsGroupedByServer;
	}

	[RelayCommand]
	private void ToggleSort()
	{
		SortByName = !SortByName;
		foreach (var server in Servers)
			server.RebuildRootGroups(SortByName);
	}

	[RelayCommand]
	private void RemoveServer(ServerGroupViewModel server)
	{
		Servers.Remove(server);
	}

	public bool IsProjectHidden(string projectId) => _hiddenProjectIds.Contains(projectId);

	[RelayCommand]
	private async Task StartServerAsync(ServerGroupViewModel server)
	{
		try
		{
			server.State = HostState.Starting;
			var providers = await _hostConnectionService.GetProvidersForProfileAsync(server.ProfileName);
			var provider = providers.FirstOrDefault(p => p.Provider.Type == server.Type).Provider;

			if (provider != null)
			{
				await provider.StartHostAsync(server.Id);
				server.State = HostState.Running;
				await LoadServerProjectsAsync(server);
			}
		}
		catch (Exception ex)
		{
			server.State = HostState.Unknown;
			server.ErrorMessage = ex.Message;
		}
	}

	[RelayCommand]
	private async Task StopServerAsync(ServerGroupViewModel server)
	{
		try
		{
			server.State = HostState.Stopping;
			var providers = await _hostConnectionService.GetProvidersForProfileAsync(server.ProfileName);
			var provider = providers.FirstOrDefault(p => p.Provider.Type == server.Type).Provider;

			if (provider != null)
			{
				await provider.StopHostAsync(server.Id);
				server.State = HostState.Stopped;
				server.Projects.Clear();
				server.IsConnected = false;
			}
		}
		catch (Exception ex)
		{
			server.State = HostState.Unknown;
			server.ErrorMessage = ex.Message;
		}
	}

	partial void OnSelectedProfileOptionChanged(Profile? value)
	{
		if (value != null && !_suppressProfileChange)
		{
			if (value != AllProfilesOption)
				_ = _profileService.SetSelectedProfileAsync(value.Name);
			_ = LoadServersAsync();
		}
	}

	private async Task LoadServersAsync()
	{
		System.Diagnostics.Debug.WriteLine($"[GodMode] LoadServersAsync: IsAllProfilesSelected={IsAllProfilesSelected}, SelectedProfile={SelectedProfile?.Name}");
		var serverList = new List<ServerGroupViewModel>();

		try
		{
			if (IsAllProfilesSelected)
			{
				var profilesForLoad = ProfileOptions.Where(p => p != AllProfilesOption).ToList();
				System.Diagnostics.Debug.WriteLine($"[GodMode] Loading servers for all {profilesForLoad.Count} profiles");
				foreach (var profile in profilesForLoad)
					await LoadServersFromProfileAsync(profile, serverList);
			}
			else if (SelectedProfile != null)
			{
				await LoadServersFromProfileAsync(SelectedProfile, serverList);
			}

			System.Diagnostics.Debug.WriteLine($"[GodMode] Total servers found: {serverList.Count}");
			Servers = new ObservableCollection<ServerGroupViewModel>(serverList);

			foreach (var server in Servers)
			{
				System.Diagnostics.Debug.WriteLine($"[GodMode] Loading projects for server: {server.Name} ({server.Id})");
				_ = LoadServerProjectsAsync(server);
			}
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[GodMode] LoadServersAsync error: {ex}");
			ErrorMessage = $"Error loading servers: {ex.Message}";
		}
	}

	private async Task LoadServersFromProfileAsync(Profile profile, List<ServerGroupViewModel> serverList)
	{
		var providers = await _hostConnectionService.GetProvidersForProfileAsync(profile.Name);

		foreach (var (provider, accountIndex) in providers)
		{
			try
			{
				var hosts = await provider.ListHostsAsync();
				foreach (var host in hosts)
				{
					var server = ServerGroupViewModel.FromHostInfo(host, profile.Name, accountIndex);
					server.IsConnected = _hostConnectionService.IsConnected(profile.Name, host.Id);
					serverList.Add(server);
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"Error loading hosts from provider: {ex.Message}");
			}
		}
	}

	private async Task LoadServerProjectsAsync(ServerGroupViewModel server)
	{
		server.IsLoadingProjects = true;
		server.ErrorMessage = null;
		System.Diagnostics.Debug.WriteLine($"[GodMode] LoadServerProjectsAsync: connecting to {server.Name}...");

		try
		{
			var connection = await _hostConnectionService.ConnectToHostAsync(server.ProfileName, server.Id);
			System.Diagnostics.Debug.WriteLine($"[GodMode] Connected to {server.Name}");
			server.IsConnected = true;
			server.State = HostState.Running;
			ConnectionStateChanged?.Invoke(true);

			// Subscribe to project creation events (once per connection)
			var connectionKey = $"{server.ProfileName}:{server.Id}";
			if (_subscribedConnections.Add(connectionKey))
			{
				connection.ProjectCreatedReceived += status =>
				{
					Dispatcher.UIThread.Post(() =>
					{
						// Add the new project to the matching server's list
						var target = Servers.FirstOrDefault(s =>
							s.ProfileName == server.ProfileName && s.Id == server.Id);
						if (target != null)
						{
							var summary = new ProjectSummary(
								status.Id, status.Name, status.State,
								status.UpdatedAt, status.CurrentQuestion, status.RootName);
							target.Projects.Insert(0, summary);
							target.RebuildRootGroups(SortByName);
						}
					});
				};
			}

			var rootsTask = connection.ListProjectRootsAsync();
			var projectsTask = connection.ListProjectsAsync();
			await Task.WhenAll(rootsTask, projectsTask);

			var roots = await rootsTask;
			var projects = await projectsTask;
			System.Diagnostics.Debug.WriteLine($"[GodMode] {server.Name}: loaded {projects.Count()} projects, {roots.Count()} roots");
			server.KnownRoots = roots.ToList();
			server.Projects = new ObservableCollection<ProjectSummary>(projects);
			server.RebuildRootGroups(SortByName);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[GodMode] LoadServerProjectsAsync error for {server.Name}: {ex.Message}");
			server.IsConnected = false;
			if (server.State is HostState.Running or HostState.Unknown)
				server.State = HostState.Stopped;
		}
		finally
		{
			server.IsLoadingProjects = false;
		}
	}

	public int GetBadgeCount(string profileName, string hostId)
		=> _notificationService.GetBadgeCount(profileName, hostId);
}
