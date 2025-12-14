using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.Maui.Services;
using GodMode.Maui.Services.Models;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using System.Collections.ObjectModel;

// Resolve ambiguous Profile type
using Profile = GodMode.Maui.Services.Models.Profile;

namespace GodMode.Maui.ViewModels;

/// <summary>
/// ViewModel for the main page showing servers grouped with their projects
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IProfileService _profileService;
    private readonly IHostConnectionService _hostConnectionService;
    private readonly INotificationService _notificationService;

    /// <summary>
    /// Special profile option representing "All" profiles
    /// </summary>
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
        IProfileService profileService,
        IHostConnectionService hostConnectionService,
        INotificationService notificationService)
    {
        _profileService = profileService;
        _hostConnectionService = hostConnectionService;
        _notificationService = notificationService;
    }

    /// <summary>
    /// Gets whether "All" profiles is selected.
    /// </summary>
    public bool IsAllProfilesSelected => SelectedProfileOption == AllProfilesOption;

    /// <summary>
    /// Gets the actual selected profile (null if "All" is selected).
    /// </summary>
    public Profile? SelectedProfile => IsAllProfilesSelected ? null : SelectedProfileOption;

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var profiles = await _profileService.GetProfilesAsync();

            // Build profile options with "All" at the top
            var options = new List<Profile> { AllProfilesOption };
            options.AddRange(profiles);
            ProfileOptions = new ObservableCollection<Profile>(options);

            // Try to restore selected profile, default to "All"
            var selectedName = await _profileService.GetSelectedProfileAsync();
            if (selectedName != null)
            {
                SelectedProfileOption = ProfileOptions.FirstOrDefault(p => p.Name == selectedName.Name) ?? AllProfilesOption;
            }
            else
            {
                SelectedProfileOption = AllProfilesOption;
            }

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

        // Load projects if expanding and not yet loaded
        if (server.IsExpanded && server.Projects.Count == 0 && server.CanConnect)
        {
            await LoadServerProjectsAsync(server);
        }
    }

    [RelayCommand]
    private async Task NavigateToProjectAsync(ProjectSummary project)
    {
        // Find the server this project belongs to
        var server = Servers.FirstOrDefault(s => s.Projects.Contains(project));
        if (server == null) return;

        await Shell.Current!.GoToAsync($"project?profileName={server.ProfileName}&hostId={server.Id}&projectId={project.Id}");
    }

    [RelayCommand]
    private async Task CreateProjectAsync(ServerGroupViewModel server)
    {
        await Shell.Current!.GoToAsync($"createProject?profileName={server.ProfileName}&hostId={server.Id}");
    }

    [RelayCommand]
    private async Task EditServerAsync(ServerGroupViewModel server)
    {
        await Shell.Current!.GoToAsync($"editServer?profileName={server.ProfileName}&accountIndex={server.AccountIndex}");
    }

    [RelayCommand]
    private async Task AddProfileAsync()
    {
        await Shell.Current!.GoToAsync("addProfile");
    }

    [RelayCommand]
    private async Task AddServerAsync()
    {
        // If a specific profile is selected, add to it; otherwise, let user choose
        if (SelectedProfile != null)
        {
            await Shell.Current!.GoToAsync($"addServer?profileName={SelectedProfile.Name}");
        }
        else if (ProfileOptions.Count > 1) // Has profiles beyond "All"
        {
            // Navigate to add server with first real profile as default
            var firstProfile = ProfileOptions.FirstOrDefault(p => p != AllProfilesOption);
            if (firstProfile != null)
            {
                await Shell.Current!.GoToAsync($"addServer?profileName={firstProfile.Name}");
            }
        }
        else
        {
            // No profiles exist, prompt to create one first
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

                // Load projects now that server is running
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
            // Save selection (but not "All" as a real profile name)
            if (value != AllProfilesOption)
            {
                _ = _profileService.SetSelectedProfileAsync(value.Name);
            }

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
                // Load servers from all profiles
                foreach (var profile in ProfileOptions.Where(p => p != AllProfilesOption))
                {
                    await LoadServersFromProfileAsync(profile, serverList);
                }
            }
            else if (SelectedProfile != null)
            {
                // Load servers from selected profile only
                await LoadServersFromProfileAsync(SelectedProfile, serverList);
            }

            Servers = new ObservableCollection<ServerGroupViewModel>(serverList);

            // Auto-load projects for running servers
            foreach (var server in Servers.Where(s => s.CanConnect))
            {
                _ = LoadServerProjectsAsync(server);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error loading servers: {ex.Message}";
        }
    }

    private async Task LoadServersFromProfileAsync(Profile profile, List<ServerGroupViewModel> serverList)
    {
        // Get providers for this profile - each account creates a provider
        var providers = await _hostConnectionService.GetProvidersForProfileAsync(profile.Name);
        var providersList = providers.ToList();

        // Track account index - providers are created in same order as accounts
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
                // Log error but continue with other providers
                System.Diagnostics.Debug.WriteLine($"Error loading hosts from provider: {ex.Message}");
            }
        }
    }

    private async Task LoadServerProjectsAsync(ServerGroupViewModel server)
    {
        if (!server.CanConnect) return;

        server.IsLoadingProjects = true;
        server.ErrorMessage = null;

        try
        {
            var connection = await _hostConnectionService.ConnectToHostAsync(server.ProfileName, server.Id);
            server.IsConnected = true;

            var projects = await connection.ListProjectsAsync();
            server.Projects = new ObservableCollection<ProjectSummary>(projects);

            // Notify property changed for computed properties
            OnPropertyChanged(nameof(server.ProjectCountDisplay));
        }
        catch (Exception ex)
        {
            server.ErrorMessage = $"Failed to load projects: {ex.Message}";
            server.IsConnected = false;
        }
        finally
        {
            server.IsLoadingProjects = false;
        }
    }

    public int GetBadgeCount(string profileName, string hostId)
    {
        return _notificationService.GetBadgeCount(profileName, hostId);
    }
}
