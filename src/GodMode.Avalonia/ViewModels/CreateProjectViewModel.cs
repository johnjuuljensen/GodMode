using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.ClientBase.Models;
using GodMode.ClientBase.Services.Models;
using GodMode.Shared.Models;
using System.Collections.ObjectModel;

namespace GodMode.Avalonia.ViewModels;

public partial class CreateProjectViewModel : ViewModelBase
{
	private readonly IProjectService _projectService;
	private readonly IProfileService _profileService;
	private readonly ICredentialService _credentialService;

	public event Action? Completed;

	/// <summary>Sentinel value for "no workspace" in the root dropdown.</summary>
	public static readonly ProjectRootInfo NoWorkspaceOption = new("None (temporary directory)");

	// Identity (set by caller)
	[ObservableProperty]
	private string _profileName = string.Empty;

	[ObservableProperty]
	private string _hostId = string.Empty;

	[ObservableProperty]
	private string _serverId = string.Empty;

	[ObservableProperty]
	private string _serverName = string.Empty;

	// Wizard: Step 0 = Workspace + Repo, Step 1 = Create
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsStep0))]
	[NotifyPropertyChangedFor(nameof(IsStep1))]
	[NotifyPropertyChangedFor(nameof(CanGoBack))]
	[NotifyPropertyChangedFor(nameof(NextButtonText))]
	private int _currentStep;

	public bool IsStep0 => CurrentStep == 0;
	public bool IsStep1 => CurrentStep == 1;
	public bool CanGoBack => CurrentStep > 0;
	public string NextButtonText => CurrentStep == 1 ? "Create" : "Next";

	// UI state
	[ObservableProperty]
	private bool _isCreating;

	[ObservableProperty]
	private bool _isLoading;

	[ObservableProperty]
	private string? _errorMessage;

	[ObservableProperty]
	private string? _creationProgressText;

	// Step 0: Workspace + Repo
	[ObservableProperty]
	private ObservableCollection<ProjectRootInfo> _projectRoots = [];

	[ObservableProperty]
	private ProjectRootInfo? _selectedProjectRoot;

	[ObservableProperty]
	private ObservableCollection<FormField> _formFields = [];

	[ObservableProperty]
	private ObservableCollection<RepoInfo> _knownRepos = [];

	[ObservableProperty]
	private RepoInfo? _selectedRepo;

	[ObservableProperty]
	private string _manualRepoUrl = string.Empty;

	// Step 1: Project name (always shown)
	[ObservableProperty]
	private string _projectName = string.Empty;

	public bool HasKnownRepos => KnownRepos.Count > 0;
	public bool IsNoWorkspace => SelectedProjectRoot == NoWorkspaceOption;
	public bool HasFormFields => FormFields.Count > 0;

	// Systems info (shown as a note on create step)
	public ObservableCollection<SystemMapping> SystemMappings { get; } = new();

	public CreateProjectViewModel(
		INavigationService navigationService,
		IProjectService projectService,
		IProfileService profileService,
		ICredentialService credentialService)
		: base(navigationService)
	{
		_projectService = projectService;
		_profileService = profileService;
		_credentialService = credentialService;
	}

	partial void OnKnownReposChanged(ObservableCollection<RepoInfo> value)
	{
		OnPropertyChanged(nameof(HasKnownRepos));
	}

	partial void OnSelectedProjectRootChanged(ProjectRootInfo? value)
	{
		if (value != null && value != NoWorkspaceOption)
		{
			var fields = FormFieldParser.Parse(value.InputSchema)
				.Where(f => f.Key != "name"); // Dedicated ProjectName field handles this
			FormFields = new ObservableCollection<FormField>(fields);
		}
		else
		{
			FormFields = [];
		}
		OnPropertyChanged(nameof(IsNoWorkspace));
		OnPropertyChanged(nameof(HasFormFields));
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
			var rootList = new List<ProjectRootInfo>(roots) { NoWorkspaceOption };
			ProjectRoots = new ObservableCollection<ProjectRootInfo>(rootList);

			SelectedProjectRoot = ProjectRoots.FirstOrDefault(r => r.IsDefault)
				?? ProjectRoots.FirstOrDefault(r => r != NoWorkspaceOption)
				?? NoWorkspaceOption;

			// Load known repos (non-critical)
			try
			{
				var repos = await _projectService.ListKnownReposAsync(ProfileName, HostId);
				KnownRepos = new ObservableCollection<RepoInfo>(repos);
			}
			catch { /* Server may not support this yet */ }
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Could not connect to server: {ex.Message}";
		}
		finally
		{
			IsLoading = false;
		}
	}

	[RelayCommand]
	private async Task NextStepAsync()
	{
		ErrorMessage = null;

		switch (CurrentStep)
		{
			case 0: // Workspace + Repo → Create
				if (SelectedProjectRoot == null)
				{
					ErrorMessage = "Please select a project root";
					return;
				}
				await LoadSystemMappingsAsync();
				CurrentStep = 1;
				break;

			case 1: // Create
				await CreateAsync();
				break;
		}
	}

	[RelayCommand]
	private void PreviousStep()
	{
		if (CurrentStep > 0)
		{
			ErrorMessage = null;
			CurrentStep--;
		}
	}

	private async Task LoadSystemMappingsAsync()
	{
		SystemMappings.Clear();

		if (string.IsNullOrEmpty(ServerId) || string.IsNullOrEmpty(ProfileName))
			return;

		try
		{
			var profile = await _profileService.GetProfileAsync(ProfileName);
			var server = profile?.Servers.FirstOrDefault(s => s.Id == ServerId);
			if (server?.Systems is not { Count: > 0 })
				return;

			var credentialNames = server.Systems.Values.Distinct();
			var resolved = await _credentialService.ResolveCredentialsAsync(ProfileName, credentialNames);

			foreach (var (systemName, credentialName) in server.Systems)
			{
				var isResolved = resolved.ContainsKey(credentialName);
				SystemMappings.Add(new SystemMapping(systemName, credentialName, isResolved));
			}
		}
		catch { /* Non-critical */ }
	}

	private async Task CreateAsync()
	{
		ErrorMessage = null;
		CreationProgressText = null;
		IsCreating = true;

		try
		{
			// Build inputs from form fields (all optional — no validation)
			var inputs = new Dictionary<string, JsonElement>();

			// Always include project name if provided
			if (!string.IsNullOrWhiteSpace(ProjectName))
				inputs["name"] = JsonSerializer.SerializeToElement(ProjectName.Trim());

			foreach (var field in FormFields)
			{
				if (!string.IsNullOrEmpty(field.Value))
				{
					inputs[field.Key] = field.FieldType == "boolean"
						? JsonSerializer.SerializeToElement(field.Value == "true" || field.Value == "True")
						: JsonSerializer.SerializeToElement(field.Value);
				}
			}

			// Add repo URL if provided
			var repoUrl = !string.IsNullOrEmpty(ManualRepoUrl) ? ManualRepoUrl
				: SelectedRepo?.CloneUrl;
			if (!string.IsNullOrEmpty(repoUrl))
				inputs["repoUrl"] = JsonSerializer.SerializeToElement(repoUrl);

			// Resolve credentials for environment injection
			Dictionary<string, string>? environment = null;
			if (SystemMappings.Count > 0)
				environment = await ResolveEnvironmentAsync();

			var rootName = IsNoWorkspace ? null : SelectedProjectRoot?.Name;

			await _projectService.CreateProjectAsync(
				ProfileName, HostId, rootName, inputs, environment);

			Completed?.Invoke();
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

	private async Task<Dictionary<string, string>?> ResolveEnvironmentAsync()
	{
		try
		{
			var profile = await _profileService.GetProfileAsync(ProfileName);
			var server = profile?.Servers.FirstOrDefault(s => s.Id == ServerId);
			if (server?.Systems is not { Count: > 0 })
				return null;

			var credentialNames = server.Systems.Values.Distinct();
			var resolved = await _credentialService.ResolveCredentialsAsync(ProfileName, credentialNames);

			var env = new Dictionary<string, string>();
			foreach (var (systemName, credentialName) in server.Systems)
			{
				if (resolved.TryGetValue(credentialName, out var value))
					env[SystemEnvMapper.GetEnvVarName(systemName)] = value;
			}

			return env.Count > 0 ? env : null;
		}
		catch { return null; }
	}

	[RelayCommand]
	private void Cancel() => Completed?.Invoke();
}
