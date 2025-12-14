using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.Maui.Services.Models;

namespace GodMode.Maui.ViewModels;

/// <summary>
/// ViewModel for editing an existing server in a profile.
/// </summary>
[QueryProperty(nameof(ProfileName), "profileName")]
[QueryProperty(nameof(AccountIndex), "accountIndex")]
public partial class EditServerViewModel : ObservableObject
{
    private readonly IProfileService _profileService;
    private Account? _originalAccount;

    [ObservableProperty]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private int _accountIndex;

    [ObservableProperty]
    private string _selectedServerType = "Local Server";

    // GitHub Codespaces fields
    [ObservableProperty]
    private string _gitHubUsername = string.Empty;

    [ObservableProperty]
    private string _gitHubToken = string.Empty;

    // Local server fields
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

    public bool IsGitHubCodespaces => SelectedServerType == "GitHub Codespaces";
    public bool IsLocalServer => SelectedServerType == "Local Server";

    public EditServerViewModel(IProfileService profileService)
    {
        _profileService = profileService;
    }

    partial void OnProfileNameChanged(string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _ = LoadAccountAsync();
        }
    }

    partial void OnAccountIndexChanged(int value)
    {
        if (!string.IsNullOrEmpty(ProfileName))
        {
            _ = LoadAccountAsync();
        }
    }

    private async Task LoadAccountAsync()
    {
        if (string.IsNullOrEmpty(ProfileName) || AccountIndex < 0)
            return;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var profile = await _profileService.GetProfileAsync(ProfileName);
            if (profile == null)
            {
                ErrorMessage = "Profile not found";
                return;
            }

            if (AccountIndex >= profile.Accounts.Count)
            {
                ErrorMessage = "Server not found";
                return;
            }

            _originalAccount = profile.Accounts[AccountIndex];

            if (_originalAccount.Type == "github")
            {
                SelectedServerType = "GitHub Codespaces";
                GitHubUsername = _originalAccount.Username ?? string.Empty;
                // Don't show the actual token for security, but indicate if one exists
                GitHubToken = string.IsNullOrEmpty(_originalAccount.Token) ? string.Empty : "********";
            }
            else
            {
                SelectedServerType = "Local Server";
                ServerUrl = _originalAccount.Path ?? "http://localhost:31337";
                ServerDisplayName = _originalAccount.Metadata?.GetValueOrDefault("name") ?? string.Empty;
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

        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            ErrorMessage = "No profile selected";
            return;
        }

        if (IsGitHubCodespaces)
        {
            if (string.IsNullOrWhiteSpace(GitHubUsername))
            {
                ErrorMessage = "Please enter your GitHub username";
                return;
            }
            // Only require token if it's a new entry or explicitly changed
            if (string.IsNullOrWhiteSpace(GitHubToken))
            {
                ErrorMessage = "Please enter your GitHub personal access token";
                return;
            }
        }
        else if (IsLocalServer)
        {
            if (string.IsNullOrWhiteSpace(ServerUrl))
            {
                ErrorMessage = "Please enter a server URL";
                return;
            }
            if (!ServerUrl.StartsWith("http://") && !ServerUrl.StartsWith("https://"))
            {
                ErrorMessage = "Server URL must start with http:// or https://";
                return;
            }
        }

        IsSaving = true;

        try
        {
            var profile = await _profileService.GetProfileAsync(ProfileName);
            if (profile == null)
            {
                ErrorMessage = "Profile not found";
                return;
            }

            if (AccountIndex >= profile.Accounts.Count)
            {
                ErrorMessage = "Server not found";
                return;
            }

            var account = profile.Accounts[AccountIndex];

            if (IsGitHubCodespaces)
            {
                account.Type = "github";
                account.Username = GitHubUsername;
                // Only update token if it was changed (not the placeholder)
                if (GitHubToken != "********")
                {
                    account.Token = GitHubToken;
                }
                account.Path = null;
                account.Metadata = null;
            }
            else
            {
                account.Type = "local";
                account.Path = ServerUrl;
                account.Username = null;
                account.Token = null;
                account.Metadata = !string.IsNullOrWhiteSpace(ServerDisplayName)
                    ? new Dictionary<string, string> { ["name"] = ServerDisplayName }
                    : null;
            }

            await _profileService.SaveProfileAsync(profile);
            await Shell.Current!.GoToAsync("..");
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

        var confirmed = await Shell.Current!.DisplayAlertAsync(
            "Delete Server",
            "Are you sure you want to remove this server from the profile?",
            "Delete",
            "Cancel");

        if (!confirmed)
            return;

        IsSaving = true;

        try
        {
            var profile = await _profileService.GetProfileAsync(ProfileName);
            if (profile == null)
            {
                ErrorMessage = "Profile not found";
                return;
            }

            if (AccountIndex >= profile.Accounts.Count)
            {
                ErrorMessage = "Server not found";
                return;
            }

            profile.Accounts.RemoveAt(AccountIndex);
            await _profileService.SaveProfileAsync(profile);
            await Shell.Current!.GoToAsync("..");
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
    private async Task CancelAsync()
    {
        await Shell.Current!.GoToAsync("..");
    }
}
