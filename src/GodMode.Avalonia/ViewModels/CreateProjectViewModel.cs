using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.ClientBase.Models;
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
	private bool _isCreating;

	[ObservableProperty]
	private bool _isLoading;

	[ObservableProperty]
	private string? _errorMessage;

	[ObservableProperty]
	private string? _creationProgressText;

	[ObservableProperty]
	private ObservableCollection<ProjectRootInfo> _projectRoots = [];

	[ObservableProperty]
	private ProjectRootInfo? _selectedProjectRoot;

	[ObservableProperty]
	private ObservableCollection<FormField> _formFields = [];

	public CreateProjectViewModel(INavigationService navigationService, IProjectService projectService)
		: base(navigationService)
	{
		_projectService = projectService;
	}

	partial void OnSelectedProjectRootChanged(ProjectRootInfo? value)
	{
		if (value != null)
		{
			var fields = FormFieldParser.Parse(value.InputSchema);
			FormFieldParser.PreserveUserValues(FormFields, fields);
			FormFields = new ObservableCollection<FormField>(fields);
		}
		else
		{
			FormFields = [];
		}
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
			ProjectRoots = new ObservableCollection<ProjectRootInfo>(roots);

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
		CreationProgressText = null;

		if (SelectedProjectRoot == null) { ErrorMessage = "Please select a project root"; return; }

		// Validate required fields
		foreach (var field in FormFields)
		{
			if (field.IsRequired && string.IsNullOrWhiteSpace(field.Value))
			{
				ErrorMessage = $"Please fill in {field.Title}";
				return;
			}
		}

		IsCreating = true;

		try
		{
			// Build inputs dictionary from form fields
			var inputs = new Dictionary<string, JsonElement>();
			foreach (var field in FormFields)
			{
				if (!string.IsNullOrEmpty(field.Value))
				{
					inputs[field.Key] = field.FieldType == "boolean"
						? JsonSerializer.SerializeToElement(field.Value == "true" || field.Value == "True")
						: JsonSerializer.SerializeToElement(field.Value);
				}
			}

			var detail = await _projectService.CreateProjectAsync(
				ProfileName, HostId,
				SelectedProjectRoot.Name,
				inputs);

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
