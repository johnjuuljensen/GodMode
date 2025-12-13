using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.Maui.Services.Models;

namespace GodMode.Maui.ViewModels;

/// <summary>
/// ViewModel for adding an account to a profile
/// </summary>
[QueryProperty(nameof(ProfileName), "profileName")]
public partial class AddAccountViewModel : ObservableObject
{
    private readonly IProfileService _profileService;

    [ObservableProperty]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private string[] _accountTypes = ["GitHub Codespaces", "Local Folder"];

    [ObservableProperty]
    private string _selectedAccountType = "Local Folder";

    [ObservableProperty]
    private string _gitHubUsername = string.Empty;

    [ObservableProperty]
    private string _gitHubToken = string.Empty;

    [ObservableProperty]
    private string _localPath = string.Empty;

    [ObservableProperty]
    private string _localDisplayName = string.Empty;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private string? _errorMessage;

    public bool IsGitHubAccount => SelectedAccountType == "GitHub Codespaces";
    public bool IsLocalAccount => SelectedAccountType == "Local Folder";

    public AddAccountViewModel(IProfileService profileService)
    {
        _profileService = profileService;
    }

    partial void OnSelectedAccountTypeChanged(string value)
    {
        OnPropertyChanged(nameof(IsGitHubAccount));
        OnPropertyChanged(nameof(IsLocalAccount));
    }

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        var page = Application.Current?.Windows[0]?.Page;
        if (page != null)
        {
            await page.DisplayAlertAsync(
                "Browse Folder",
                "Please enter the folder path manually in the text field.",
                "OK");
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

        if (IsGitHubAccount)
        {
            if (string.IsNullOrWhiteSpace(GitHubUsername))
            {
                ErrorMessage = "Please enter your GitHub username";
                return;
            }
            if (string.IsNullOrWhiteSpace(GitHubToken))
            {
                ErrorMessage = "Please enter your GitHub token";
                return;
            }
        }
        else if (IsLocalAccount)
        {
            if (string.IsNullOrWhiteSpace(LocalPath))
            {
                ErrorMessage = "Please enter a folder path";
                return;
            }
            if (!Directory.Exists(LocalPath))
            {
                ErrorMessage = "The specified folder does not exist";
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
                    Path = LocalPath,
                    Metadata = !string.IsNullOrWhiteSpace(LocalDisplayName)
                        ? new Dictionary<string, string> { ["name"] = LocalDisplayName }
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
