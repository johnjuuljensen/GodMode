using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.Shared.Models;
using GodMode.Shared.Enums;
using System.Collections.ObjectModel;

namespace GodMode.Avalonia.ViewModels;

public partial class HostViewModel : ViewModelBase
{
	private readonly IServerConnectionService _hostConnectionService;
	private readonly IProjectService _projectService;
	private readonly INotificationService _notificationService;

	[ObservableProperty]
	private string _profileName = string.Empty;

	[ObservableProperty]
	private string _hostId = string.Empty;

	[ObservableProperty]
	private ServerStatus? _serverStatus;

	[ObservableProperty]
	private ObservableCollection<ProjectSummary> _projects = new();

	[ObservableProperty]
	private bool _isLoading;

	[ObservableProperty]
	private string? _errorMessage;

	[ObservableProperty]
	private bool _isConnected;

	public HostViewModel(
		INavigationService navigationService,
		IServerConnectionService hostConnectionService,
		IProjectService projectService,
		INotificationService notificationService)
		: base(navigationService)
	{
		_hostConnectionService = hostConnectionService;
		_projectService = projectService;
		_notificationService = notificationService;
	}

	partial void OnProfileNameChanged(string value)
	{
		if (!string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(HostId))
			_ = LoadAsync();
	}

	partial void OnHostIdChanged(string value)
	{
		if (!string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(ProfileName))
			_ = LoadAsync();
	}

	[RelayCommand]
	private async Task LoadAsync()
	{
		if (string.IsNullOrEmpty(ProfileName) || string.IsNullOrEmpty(HostId))
			return;

		IsLoading = true;
		ErrorMessage = null;

		try
		{
			IsConnected = _hostConnectionService.IsConnected(ProfileName, HostId);
			await LoadServerStatusAsync();
			await LoadProjectsAsync();
			IsConnected = true;
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error loading host: {ex.Message}";
			IsConnected = false;
		}
		finally
		{
			IsLoading = false;
		}
	}

	[RelayCommand]
	private void GoBack() => Navigation.GoBack();

	[RelayCommand]
	private async Task RefreshAsync() => await LoadAsync();

	[RelayCommand]
	private async Task StartServerAsync()
	{
		if (ServerStatus == null) return;

		IsLoading = true;
		ErrorMessage = null;

		try
		{
			var providers = await _hostConnectionService.GetProvidersForProfileAsync(ProfileName);
			var provider = providers.FirstOrDefault(p => p.Provider.Type == ServerStatus.Type.ToString().ToLower()).Provider;

			if (provider != null)
			{
				await provider.StartHostAsync(HostId);
				await Task.Delay(2000);
				await LoadServerStatusAsync();
			}
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error starting host: {ex.Message}";
		}
		finally
		{
			IsLoading = false;
		}
	}

	[RelayCommand]
	private async Task StopServerAsync()
	{
		if (ServerStatus == null) return;

		IsLoading = true;
		ErrorMessage = null;

		try
		{
			var providers = await _hostConnectionService.GetProvidersForProfileAsync(ProfileName);
			var provider = providers.FirstOrDefault(p => p.Provider.Type == ServerStatus.Type.ToString().ToLower()).Provider;

			if (provider != null)
			{
				await provider.StopHostAsync(HostId);
				await LoadServerStatusAsync();
			}
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error stopping host: {ex.Message}";
		}
		finally
		{
			IsLoading = false;
		}
	}

	[RelayCommand]
	private void CreateProject()
	{
		Navigation.NavigateTo<CreateProjectViewModel>(vm =>
		{
			vm.ProfileName = ProfileName;
			vm.HostId = HostId;
		});
	}

	[RelayCommand]
	private void NavigateToProject(ProjectSummary project)
	{
		if (project == null) return;

		_notificationService.ClearBadgeCountForProject(ProfileName, HostId, project.Id);

		Navigation.NavigateTo<ProjectViewModel>(vm =>
		{
			vm.ProfileName = ProfileName;
			vm.HostId = HostId;
			vm.ProjectId = project.Id;
		});
	}

	private async Task LoadServerStatusAsync()
	{
		var providers = await _hostConnectionService.GetProvidersForProfileAsync(ProfileName);

		foreach (var (provider, _) in providers)
		{
			try
			{
				var hosts = await provider.ListHostsAsync();
				var host = hosts.FirstOrDefault(h => h.Id == HostId);

				if (host != null)
				{
					ServerStatus = await provider.GetServerStatusAsync(HostId);
					return;
				}
			}
			catch { /* Try next provider */ }
		}

		throw new InvalidOperationException($"Host {HostId} not found");
	}

	private async Task LoadProjectsAsync()
	{
		var projects = await _projectService.ListProjectsAsync(ProfileName, HostId);
		Projects = new ObservableCollection<ProjectSummary>(projects);
	}

	public int GetBadgeCount() => _notificationService.GetBadgeCount(ProfileName, HostId);
}
