using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using System.Collections.ObjectModel;

namespace GodMode.Avalonia.ViewModels;

public partial class CreateProjectViewModel : ViewModelBase
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
	private bool _isLoading;

	[ObservableProperty]
	private string? _errorMessage;

	[ObservableProperty]
	private ObservableCollection<ProjectRoot> _projectRoots = [];

	[ObservableProperty]
	private ProjectRoot? _selectedProjectRoot;

	[ObservableProperty]
	private ProjectType[] _projectTypes = [ProjectType.RawFolder, ProjectType.GitHubRepo, ProjectType.GitHubWorktree];

	[ObservableProperty]
	private ProjectType _selectedProjectType = ProjectType.RawFolder;

	public bool RequiresRepoUrl => SelectedProjectType is ProjectType.GitHubRepo or ProjectType.GitHubWorktree;

	public CreateProjectViewModel(INavigationService navigationService, IProjectService projectService)
		: base(navigationService)
	{
		_projectService = projectService;
	}

	partial void OnSelectedProjectTypeChanged(ProjectType value)
	{
		OnPropertyChanged(nameof(RequiresRepoUrl));
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
			var roots = await _projectService.ListProjectRootsAsync(ProfileName, HostId);
			ProjectRoots = new ObservableCollection<ProjectRoot>(roots);

			if (ProjectRoots.Count > 0)
				SelectedProjectRoot = ProjectRoots[0];
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error loading project roots: {ex.Message}";
		}
		finally
		{
			IsLoading = false;
		}
	}

	[RelayCommand]
	private async Task CreateAsync()
	{
		ErrorMessage = null;

		if (string.IsNullOrWhiteSpace(ProjectName)) { ErrorMessage = "Please enter a project name"; return; }
		if (SelectedProjectRoot == null) { ErrorMessage = "Please select a project root"; return; }
		if (RequiresRepoUrl && string.IsNullOrWhiteSpace(RepoUrl)) { ErrorMessage = "Please enter a repository URL"; return; }
		if (string.IsNullOrWhiteSpace(InitialPrompt)) { ErrorMessage = "Please enter an initial prompt"; return; }

		IsCreating = true;

		try
		{
			var detail = await _projectService.CreateProjectAsync(
				ProfileName, HostId, ProjectName,
				SelectedProjectRoot.Name, SelectedProjectType,
				string.IsNullOrWhiteSpace(RepoUrl) ? null : RepoUrl,
				InitialPrompt);

			// Navigate back then to the new project
			Navigation.GoBack();
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
	private void Cancel() => Navigation.GoBack();
}
