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
	private readonly INotificationService _notificationService;

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

	public MainViewModel(
		INavigationService navigationService,
		IProfileService profileService,
		IHostConnectionService hostConnectionService,
		INotificationService notificationService)
		: base(navigationService)
	{
		_profileService = profileService;
		_hostConnectionService = hostConnectionService;
		_notificationService = notificationService;
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
			var profiles = await _profileService.GetProfilesAsync();
			var options = new List<Profile> { AllProfilesOption };
			options.AddRange(profiles);
			ProfileOptions = new ObservableCollection<Profile>(options);

			var selectedName = await _profileService.GetSelectedProfileAsync();
			SelectedProfileOption = selectedName != null
				? ProfileOptions.FirstOrDefault(p => p.Name == selectedName.Name) ?? AllProfilesOption
				: AllProfilesOption;

			await LoadServersAsync();
		}
		catch (Exception ex)
		{
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
	private void NavigateToProject(ProjectSummary project)
	{
		var server = Servers.FirstOrDefault(s => s.Projects.Contains(project));
		if (server == null) return;

		Navigation.NavigateTo<ProjectViewModel>(vm =>
		{
			vm.ProfileName = server.ProfileName;
			vm.HostId = server.Id;
			vm.ProjectId = project.Id;
		});
	}

	[RelayCommand]
	private void CreateProject(ServerGroupViewModel server)
	{
		Navigation.NavigateTo<CreateProjectViewModel>(vm =>
		{
			vm.ProfileName = server.ProfileName;
			vm.HostId = server.Id;
		});
	}

	[RelayCommand]
	private void EditServer(ServerGroupViewModel server)
	{
		Navigation.NavigateTo<EditServerViewModel>(vm =>
		{
			vm.ProfileName = server.ProfileName;
			vm.AccountIndex = server.AccountIndex;
		});
	}

	[RelayCommand]
	private void AddProfile()
	{
		Navigation.NavigateTo<AddProfileViewModel>();
	}

	[RelayCommand]
	private void AddServer()
	{
		if (SelectedProfile != null)
		{
			Navigation.NavigateTo<AddServerViewModel>(vm => vm.ProfileName = SelectedProfile.Name);
		}
		else if (ProfileOptions.Count > 1)
		{
			var firstProfile = ProfileOptions.FirstOrDefault(p => p != AllProfilesOption);
			if (firstProfile != null)
				Navigation.NavigateTo<AddServerViewModel>(vm => vm.ProfileName = firstProfile.Name);
		}
		else
		{
			ErrorMessage = "Create a profile first before adding servers";
		}
	}

	[RelayCommand]
	private async Task StartServerAsync(ServerGroupViewModel server)
	{
		try
		{
			server.State = HostState.Starting;
			var providers = await _hostConnectionService.GetProvidersForProfileAsync(server.ProfileName);
			var provider = providers.FirstOrDefault(p => p.Type == server.Type);

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
			var provider = providers.FirstOrDefault(p => p.Type == server.Type);

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
		if (value != null)
		{
			if (value != AllProfilesOption)
				_ = _profileService.SetSelectedProfileAsync(value.Name);
			_ = LoadServersAsync();
		}
	}

	private async Task LoadServersAsync()
	{
		var serverList = new List<ServerGroupViewModel>();

		try
		{
			if (IsAllProfilesSelected)
			{
				foreach (var profile in ProfileOptions.Where(p => p != AllProfilesOption))
					await LoadServersFromProfileAsync(profile, serverList);
			}
			else if (SelectedProfile != null)
			{
				await LoadServersFromProfileAsync(SelectedProfile, serverList);
			}

			Servers = new ObservableCollection<ServerGroupViewModel>(serverList);

			foreach (var server in Servers)
				_ = LoadServerProjectsAsync(server);
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error loading servers: {ex.Message}";
		}
	}

	private async Task LoadServersFromProfileAsync(Profile profile, List<ServerGroupViewModel> serverList)
	{
		var providers = await _hostConnectionService.GetProvidersForProfileAsync(profile.Name);
		var providersList = providers.ToList();

		for (int accountIndex = 0; accountIndex < providersList.Count; accountIndex++)
		{
			var provider = providersList[accountIndex];
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

		try
		{
			var connection = await _hostConnectionService.ConnectToHostAsync(server.ProfileName, server.Id);
			server.IsConnected = true;
			server.State = HostState.Running;

			var projects = await connection.ListProjectsAsync();
			server.Projects = new ObservableCollection<ProjectSummary>(projects);
		}
		catch (Exception)
		{
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
