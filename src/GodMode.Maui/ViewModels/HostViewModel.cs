using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.Maui.Services;
using GodMode.Shared.Models;
using GodMode.Shared.Enums;
using System.Collections.ObjectModel;

namespace GodMode.Maui.ViewModels;

/// <summary>
/// ViewModel for the host page showing host status and projects
/// </summary>
[QueryProperty(nameof(ProfileName), "profileName")]
[QueryProperty(nameof(HostId), "hostId")]
public partial class HostViewModel : ObservableObject
{
    private readonly HostConnectionService _hostConnectionService;
    private readonly ProjectService _projectService;
    private readonly NotificationService _notificationService;

    [ObservableProperty]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private string _hostId = string.Empty;

    [ObservableProperty]
    private HostStatus? _hostStatus;

    [ObservableProperty]
    private ObservableCollection<ProjectSummary> _projects = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isConnected;

    public HostViewModel(
        HostConnectionService hostConnectionService,
        ProjectService projectService,
        NotificationService notificationService)
    {
        _hostConnectionService = hostConnectionService;
        _projectService = projectService;
        _notificationService = notificationService;
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
            // Check connection status
            IsConnected = _hostConnectionService.IsConnected(ProfileName, HostId);

            // Load host status (this will connect if needed)
            await LoadHostStatusAsync();

            // Load projects
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
    private async Task RefreshAsync()
    {
        await LoadAsync();
    }

    [RelayCommand]
    private async Task StartHostAsync()
    {
        if (HostStatus == null) return;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var providers = await _hostConnectionService.GetProvidersForProfileAsync(ProfileName);
            var provider = providers.FirstOrDefault(p => p.Type == HostStatus.Type.ToString().ToLower());

            if (provider != null)
            {
                await provider.StartHostAsync(HostId);
                await Task.Delay(2000); // Give it time to start
                await LoadHostStatusAsync();
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
    private async Task StopHostAsync()
    {
        if (HostStatus == null) return;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var providers = await _hostConnectionService.GetProvidersForProfileAsync(ProfileName);
            var provider = providers.FirstOrDefault(p => p.Type == HostStatus.Type.ToString().ToLower());

            if (provider != null)
            {
                await provider.StopHostAsync(HostId);
                await LoadHostStatusAsync();
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
    private async Task CreateProjectAsync()
    {
        // Navigate to create project page
        await Shell.Current.GoToAsync($"createProject?profileName={ProfileName}&hostId={HostId}");
    }

    [RelayCommand]
    private async Task NavigateToProjectAsync(ProjectSummary project)
    {
        if (project == null) return;

        // Clear badge for this project
        _notificationService.ClearBadgeCountForProject(ProfileName, HostId, project.Id);

        await Shell.Current.GoToAsync($"project?profileName={ProfileName}&hostId={HostId}&projectId={project.Id}");
    }

    private async Task LoadHostStatusAsync()
    {
        var providers = await _hostConnectionService.GetProvidersForProfileAsync(ProfileName);

        foreach (var provider in providers)
        {
            try
            {
                var hosts = await provider.ListHostsAsync();
                var host = hosts.FirstOrDefault(h => h.Id == HostId);

                if (host != null)
                {
                    HostStatus = await provider.GetHostStatusAsync(HostId);
                    return;
                }
            }
            catch
            {
                // Try next provider
            }
        }

        throw new InvalidOperationException($"Host {HostId} not found");
    }

    private async Task LoadProjectsAsync()
    {
        var projects = await _projectService.ListProjectsAsync(ProfileName, HostId);
        Projects = new ObservableCollection<ProjectSummary>(projects);
    }

    public int GetBadgeCount()
    {
        return _notificationService.GetBadgeCount(ProfileName, HostId);
    }
}
