using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.Maui.Services;

namespace GodMode.Maui.ViewModels;

/// <summary>
/// ViewModel for creating a new project
/// </summary>
[QueryProperty(nameof(ProfileName), "profileName")]
[QueryProperty(nameof(HostId), "hostId")]
public partial class CreateProjectViewModel : ObservableObject
{
    private readonly IProjectService _projectService;

    [ObservableProperty]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private string _hostId = string.Empty;

    [ObservableProperty]
    private string _projectName = string.Empty;

    [ObservableProperty]
    private string? _repoUrl;

    [ObservableProperty]
    private string _initialPrompt = string.Empty;

    [ObservableProperty]
    private bool _isCreating;

    [ObservableProperty]
    private string? _errorMessage;

    public CreateProjectViewModel(IProjectService projectService)
    {
        _projectService = projectService;
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        ErrorMessage = null;

        // Validate
        if (string.IsNullOrWhiteSpace(ProjectName))
        {
            ErrorMessage = "Please enter a project name";
            return;
        }

        if (string.IsNullOrWhiteSpace(InitialPrompt))
        {
            ErrorMessage = "Please enter an initial prompt";
            return;
        }

        IsCreating = true;

        try
        {
            var detail = await _projectService.CreateProjectAsync(
                ProfileName,
                HostId,
                ProjectName,
                string.IsNullOrWhiteSpace(RepoUrl) ? null : RepoUrl,
                InitialPrompt);

            // Navigate to the new project
            await Shell.Current.GoToAsync($"..?projectId={detail.Status.Id}");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error creating project: {ex.Message}";
        }
        finally
        {
            IsCreating = false;
        }
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        await Shell.Current.GoToAsync("..");
    }
}
