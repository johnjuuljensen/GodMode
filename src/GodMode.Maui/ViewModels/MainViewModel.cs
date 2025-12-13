using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.Maui.Services;
using GodMode.Maui.Services.Models;
using GodMode.Shared.Enums;
using System.Collections.ObjectModel;
using HostInfo = GodMode.Shared.Models.HostInfo;

namespace GodMode.Maui.ViewModels;

/// <summary>
/// ViewModel for the main page showing profiles and hosts
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IProfileService _profileService;
    private readonly IHostConnectionService _hostConnectionService;
    private readonly INotificationService _notificationService;

    [ObservableProperty]
    private ObservableCollection<Profile> _profiles = new();

    [ObservableProperty]
    private Profile? _selectedProfile;

    [ObservableProperty]
    private ObservableCollection<HostInfo> _hosts = new();

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

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var profiles = await _profileService.GetProfilesAsync();
            Profiles = new ObservableCollection<Profile>(profiles);

            if (Profiles.Any())
            {
                SelectedProfile = await _profileService.GetSelectedProfileAsync() ?? Profiles.First();
                await LoadHostsAsync();
            }
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
    private async Task RefreshHostsAsync()
    {
        if (SelectedProfile == null) return;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            await LoadHostsAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error refreshing hosts: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SelectProfileAsync(Profile profile)
    {
        if (profile == null) return;

        SelectedProfile = profile;
        await _profileService.SetSelectedProfileAsync(profile.Name);
        await LoadHostsAsync();
    }

    [RelayCommand]
    private async Task NavigateToHostAsync(HostInfo host)
    {
        if (SelectedProfile == null || host == null) return;

        // Navigate to host page (this would be handled by the shell navigation)
        await Shell.Current.GoToAsync($"host?profileName={SelectedProfile.Name}&hostId={host.Id}");
    }

    [RelayCommand]
    private async Task AddProfileAsync()
    {
        // Navigate to add profile page
        await Shell.Current.GoToAsync("addProfile");
    }

    partial void OnSelectedProfileChanged(Profile? value)
    {
        if (value != null)
        {
            _ = LoadHostsAsync();
        }
    }

    private async Task LoadHostsAsync()
    {
        if (SelectedProfile == null) return;

        var hosts = await _hostConnectionService.ListAllHostsAsync(SelectedProfile.Name);
        Hosts = new ObservableCollection<HostInfo>(hosts);
    }

    public int GetBadgeCount(string hostId)
    {
        if (SelectedProfile == null) return 0;
        return _notificationService.GetBadgeCount(SelectedProfile.Name, hostId);
    }
}
