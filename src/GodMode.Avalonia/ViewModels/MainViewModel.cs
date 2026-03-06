using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using System.Collections.ObjectModel;

namespace GodMode.Avalonia.ViewModels;

public partial class MainViewModel : ViewModelBase
{
	private readonly IServerRegistryService _serverRegistry;
	private readonly IHostConnectionService _hostConnectionService;
	private readonly IProjectService _projectService;
	private readonly INotificationService _notificationService;
	private readonly IDialogService _dialogService;
	private readonly HashSet<string> _subscribedConnections = new();
	private readonly HashSet<string> _hiddenProjectIds = new();

	/// <summary>
	/// Internal list of servers for connection tracking.
	/// </summary>
	private readonly List<ServerGroupViewModel> _serverConnections = new();

	// Events for shell orchestration
	public event Action<RootGroupViewModel, ProjectSummary>? ProjectSelected;
	public event Action<RootGroupViewModel, string?>? CreateProjectRequested;
	public event Action? AddServerRequested;
	public event Action<ServerGroupViewModel>? EditServerRequested;
	public event Action<bool>? ConnectionStateChanged;
	public event Action<string, ProjectStatus>? ServerStatusChanged;

	/// <summary>
	/// Profile filter options: "All" + discovered profile names.
	/// </summary>
	[ObservableProperty]
	private ObservableCollection<string> _profileFilterOptions = new() { "All" };

	[ObservableProperty]
	private string _selectedProfileFilter = "All";

	/// <summary>
	/// Main display hierarchy: Profile → Root:Server → Projects.
	/// </summary>
	[ObservableProperty]
	private ObservableCollection<ProfileGroupViewModel> _profileGroups = new();

	/// <summary>
	/// Flat server list kept for backward compat (tile view, status updates).
	/// </summary>
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

	/// <summary>
	/// Servers that don't appear under any profile group (disconnected or no roots).
	/// Shown in the [Inactive] section so they can be edited/started.
	/// </summary>
	[ObservableProperty]
	private ObservableCollection<ServerGroupViewModel> _inactiveServers = new();

	[ObservableProperty]
	private bool _isInactiveExpanded = true;

	public MainViewModel(
		INavigationService navigationService,
		IServerRegistryService serverRegistry,
		IHostConnectionService hostConnectionService,
		IProjectService projectService,
		INotificationService notificationService,
		IDialogService dialogService)
		: base(navigationService)
	{
		_serverRegistry = serverRegistry;
		_hostConnectionService = hostConnectionService;
		_projectService = projectService;
		_notificationService = notificationService;
		_dialogService = dialogService;
	}

	[RelayCommand]
	private async Task LoadAsync()
	{
		IsLoading = true;
		ErrorMessage = null;

		try
		{
			await LoadServersAsync();
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error loading servers: {ex.Message}";
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
	private void SelectProject(ProjectSummary project)
	{
		// Find the root group containing this project
		foreach (var profileGroup in ProfileGroups)
		{
			foreach (var rootGroup in profileGroup.RootGroups)
			{
				if (rootGroup.Projects.Any(p => p.Id == project.Id))
				{
					SelectedProject = project;
					ProjectSelected?.Invoke(rootGroup, project);
					return;
				}
			}
		}
	}

	[RelayCommand]
	private void CreateProjectForRoot(RootGroupViewModel root)
	{
		CreateProjectRequested?.Invoke(root, null);
	}

	[RelayCommand]
	private void CreateProjectForRootAction(RootActionItem item)
	{
		CreateProjectRequested?.Invoke(item.Root, item.Action.Name);
	}

	[RelayCommand]
	private void EditServer(ServerGroupViewModel server)
	{
		EditServerRequested?.Invoke(server);
	}

	[RelayCommand]
	private void AddServer()
	{
		AddServerRequested?.Invoke();
	}

	[RelayCommand]
	private void HideProject(ProjectSummary project)
	{
		_hiddenProjectIds.Add(project.Id);
		HiddenProjectCount = _hiddenProjectIds.Count;

		if (!ShowHiddenProjects)
		{
			foreach (var profileGroup in ProfileGroups)
			{
				foreach (var rootGroup in profileGroup.RootGroups)
				{
					var found = rootGroup.Projects.FirstOrDefault(p => p.Id == project.Id);
					if (found != null)
					{
						rootGroup.Projects.Remove(found);
						return;
					}
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
		// Find which root group this project belongs to
		RootGroupViewModel? ownerRoot = null;
		foreach (var profileGroup in ProfileGroups)
		{
			foreach (var rootGroup in profileGroup.RootGroups)
			{
				if (rootGroup.Projects.Contains(project))
				{
					ownerRoot = rootGroup;
					break;
				}
			}
			if (ownerRoot != null) break;
		}

		if (ownerRoot == null) return;

		var (confirmed, force) = await _dialogService.ConfirmDeleteAsync(project.Name);
		if (!confirmed) return;

		try
		{
			// ProfileName here is the client-side connection profile (ignored by backend now)
			await _projectService.DeleteProjectAsync(ownerRoot.ProfileName, ownerRoot.HostId, project.Id, force);
			ownerRoot.Projects.Remove(project);
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
	private void ToggleInactiveExpanded()
	{
		IsInactiveExpanded = !IsInactiveExpanded;
	}

	[RelayCommand]
	private void ToggleSort()
	{
		SortByName = !SortByName;
		RebuildProfileGroups();
	}

	[RelayCommand]
	private void RemoveServer(ServerGroupViewModel server)
	{
		Servers.Remove(server);
		_serverConnections.Remove(server);
		RebuildProfileGroups();
	}

	public bool IsProjectHidden(string projectId) => _hiddenProjectIds.Contains(projectId);

	[RelayCommand]
	private async Task StartServerAsync(ServerGroupViewModel server)
	{
		try
		{
			server.State = HostState.Starting;
			var providers = await _hostConnectionService.GetAllProvidersAsync();
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
			var providers = await _hostConnectionService.GetAllProvidersAsync();
			var provider = providers.FirstOrDefault(p => p.Provider.Type == server.Type).Provider;

			if (provider != null)
			{
				await provider.StopHostAsync(server.Id);
				server.State = HostState.Stopped;
				server.Projects.Clear();
				server.IsConnected = false;
				RebuildProfileGroups();
			}
		}
		catch (Exception ex)
		{
			server.State = HostState.Unknown;
			server.ErrorMessage = ex.Message;
		}
	}

	partial void OnSelectedProfileFilterChanged(string value)
	{
		RebuildProfileGroups();
	}

	private async Task LoadServersAsync()
	{
		var serverList = new List<ServerGroupViewModel>();

		try
		{
			var providers = await _hostConnectionService.GetAllProvidersAsync();

			foreach (var (provider, serverIndex) in providers)
			{
				try
				{
					var hosts = await provider.ListHostsAsync();
					foreach (var host in hosts)
					{
						var server = ServerGroupViewModel.FromHostInfo(host, "", serverIndex);
						server.IsConnected = _hostConnectionService.IsConnected(host.Id);
						serverList.Add(server);
					}
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"Error loading hosts from provider: {ex.Message}");
				}
			}

			Servers = new ObservableCollection<ServerGroupViewModel>(serverList);
			_serverConnections.Clear();
			_serverConnections.AddRange(serverList);

			// Load projects for each server (also discovers profiles)
			foreach (var server in _serverConnections)
			{
				_ = LoadServerProjectsAsync(server);
			}
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error loading servers: {ex.Message}";
		}
	}

	private async Task LoadServerProjectsAsync(ServerGroupViewModel server)
	{
		server.IsLoadingProjects = true;
		server.ErrorMessage = null;

		try
		{
			var connection = await _hostConnectionService.ConnectToHostAsync(server.Id);
			server.IsConnected = true;
			server.State = HostState.Running;
			ConnectionStateChanged?.Invoke(true);

			// Subscribe to project creation events (once per connection)
			if (_subscribedConnections.Add(server.Id))
			{
				connection.ProjectCreatedReceived += status =>
				{
					Dispatcher.UIThread.Post(() =>
					{
						var target = _serverConnections.FirstOrDefault(s => s.Id == server.Id);
						if (target != null)
						{
							var summary = new ProjectSummary(
								status.Id, status.Name, status.State,
								status.UpdatedAt, status.CurrentQuestion, status.RootName,
								status.ProfileName);
							target.Projects.Insert(0, summary);
							target.RebuildRootGroups(SortByName);
							RebuildProfileGroups();
						}
					});
				};

				connection.ProjectDeletedReceived += projectId =>
				{
					Dispatcher.UIThread.Post(() =>
					{
						var target = _serverConnections.FirstOrDefault(s => s.Id == server.Id);
						if (target != null)
						{
							var project = target.Projects.FirstOrDefault(p => p.Id == projectId);
							if (project != null)
							{
								target.Projects.Remove(project);
								target.RebuildRootGroups(SortByName);
								RebuildProfileGroups();
							}
						}
					});
				};

				connection.StatusChangedReceived += (projectId, status) =>
				{
					ServerStatusChanged?.Invoke(projectId, status);
				};
			}

			var rootsTask = connection.ListProjectRootsAsync();
			var projectsTask = connection.ListProjectsAsync();
			await Task.WhenAll(rootsTask, projectsTask);

			var roots = await rootsTask;
			var projects = await projectsTask;
			server.KnownRoots = roots.ToList();
			server.Projects = new ObservableCollection<ProjectSummary>(projects);
			server.RebuildRootGroups(SortByName);

			RebuildProfileGroups();
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"LoadServerProjectsAsync error for {server.Name}: {ex.Message}");
			server.IsConnected = false;
			if (server.State is HostState.Running or HostState.Unknown)
				server.State = HostState.Stopped;
		}
		finally
		{
			server.IsLoadingProjects = false;
		}
	}

	/// <summary>
	/// Rebuilds the ProfileGroups hierarchy from all connected servers' data.
	/// Groups: Profile → Root:Server → Projects.
	/// </summary>
	private void RebuildProfileGroups()
	{
		var profileDict = new Dictionary<string, List<RootGroupViewModel>>(StringComparer.OrdinalIgnoreCase);
		var allProfileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var server in _serverConnections)
		{
			if (!server.IsConnected) continue;

			foreach (var rootGroup in server.RootGroups)
			{
				// Each root has projects with ProfileName set
				// Group projects by their profile name
				var projectsByProfile = rootGroup.Projects
					.GroupBy(p => p.ProfileName ?? "Default")
					.ToDictionary(g => g.Key, g => g.ToList());

				// Also include empty roots via KnownRoots
				var rootInfo = server.KnownRoots.FirstOrDefault(r => r.Name == rootGroup.Name);
				var rootProfileName = rootInfo?.ProfileName ?? "Default";
				allProfileNames.Add(rootProfileName);

				// Ensure at least the root's profile is represented (even with no projects)
				if (!projectsByProfile.ContainsKey(rootProfileName))
					projectsByProfile[rootProfileName] = new List<ProjectSummary>();

				foreach (var (profileName, projects) in projectsByProfile)
				{
					allProfileNames.Add(profileName);

					if (!profileDict.TryGetValue(profileName, out var rootList))
					{
						rootList = new List<RootGroupViewModel>();
						profileDict[profileName] = rootList;
					}

					// Check if we already have this root:server combination
					var existingRoot = rootList.FirstOrDefault(r =>
						r.RootName == rootGroup.Name && r.HostId == server.Id);

					if (existingRoot != null)
					{
						// Merge projects
						foreach (var p in projects)
						{
							if (!existingRoot.Projects.Any(ep => ep.Id == p.Id))
								existingRoot.Projects.Add(p);
						}
					}
					else
					{
						var sorted = SortByName
							? projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
							: projects.OrderByDescending(p => p.UpdatedAt);

						var newRoot = new RootGroupViewModel
						{
							Name = _serverConnections.Count > 1
								? $"{rootGroup.Name} ({server.Name})"
								: rootGroup.Name,
							RootName = rootGroup.Name,
							ProfileName = profileName,
							HostId = server.Id,
							HostDisplayName = server.Name,
							IsConnected = server.IsConnected,
							Server = server,
							Projects = new ObservableCollection<ProjectSummary>(sorted)
						};

						// Copy action items from the server's root info
						var actions = rootInfo?.Actions ?? [];
						newRoot.ActionItems = actions.Select(a => new RootActionItem(newRoot, a)).ToList();

						rootList.Add(newRoot);
					}
				}
			}
		}

		// Apply profile filter
		var filter = SelectedProfileFilter;
		var filteredProfiles = filter == "All"
			? profileDict
			: profileDict.Where(kvp => kvp.Key.Equals(filter, StringComparison.OrdinalIgnoreCase))
				.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

		var groups = filteredProfiles
			.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
			.Select(kvp => new ProfileGroupViewModel
			{
				Name = kvp.Key,
				RootGroups = new ObservableCollection<RootGroupViewModel>(kvp.Value)
			});

		ProfileGroups = new ObservableCollection<ProfileGroupViewModel>(groups);

		// Identify servers not represented in any profile group
		var representedServerIds = profileDict.Values
			.SelectMany(roots => roots)
			.Select(r => r.HostId)
			.ToHashSet();

		InactiveServers = new ObservableCollection<ServerGroupViewModel>(
			_serverConnections.Where(s => !representedServerIds.Contains(s.Id)));

		// Update filter options
		var options = new List<string> { "All" };
		options.AddRange(allProfileNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
		if (!options.SequenceEqual(ProfileFilterOptions))
		{
			ProfileFilterOptions = new ObservableCollection<string>(options);
			// Restore selection
			if (!ProfileFilterOptions.Contains(SelectedProfileFilter))
				SelectedProfileFilter = "All";
		}
	}

	public int GetBadgeCount(string profileName, string hostId)
		=> _notificationService.GetBadgeCount(profileName, hostId);
}
