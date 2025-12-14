using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.Maui.Services.Models;

namespace GodMode.Maui.ViewModels;

/// <summary>
/// ViewModel for adding an account to a profile.
/// Accounts define how to connect to servers that host projects.
/// </summary>
[QueryProperty(nameof(ProfileName), "profileName")]
public partial class AddAccountViewModel : ObservableObject
{
    private readonly IProfileService _profileService;

    [ObservableProperty]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private string[] _accountTypes = ["GitHub Codespaces", "Local Server"];

    [ObservableProperty]
    private string _selectedAccountType = "Local Server";

    // GitHub Codespaces account fields
    [ObservableProperty]
    private string _gitHubUsername = string.Empty;

    [ObservableProperty]
    private string _gitHubToken = string.Empty;

    // Local server account fields
    [ObservableProperty]
    private string _serverUrl = "http://localhost:5000";

    [ObservableProperty]
    private string _serverDisplayName = string.Empty;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private string? _errorMessage;

    public bool IsGitHubAccount => SelectedAccountType == "GitHub Codespaces";
    public bool IsLocalServer => SelectedAccountType == "Local Server";

    public AddAccountViewModel(IProfileService profileService)
    {
        _profileService = profileService;
    }

    partial void OnSelectedAccountTypeChanged(string value)
    {
        OnPropertyChanged(nameof(IsGitHubAccount));
        OnPropertyChanged(nameof(IsLocalServer));
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

        if (IsGitHubAccount)
        {
            if (string.IsNullOrWhiteSpace(GitHubUsername))
            {
                ErrorMessage = "Please enter your GitHub username";
                return;
            }
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

            var account = IsGitHubAccount
                ? new Account
                {
                    Type = "github",
                    Username = GitHubUsername,
                    Token = GitHubToken
                }
                : new Account
                {
                    Type = "local",
                    Path = ServerUrl,
                    Metadata = !string.IsNullOrWhiteSpace(ServerDisplayName)
                        ? new Dictionary<string, string> { ["name"] = ServerDisplayName }
                        : null
                };

            profile.Accounts.Add(account);
            await _profileService.SaveProfileAsync(profile);
            await Shell.Current!.GoToAsync("..");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error adding account: {ex.Message}";
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
