using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.ClientBase.Services.Models;
using System.Collections.ObjectModel;

namespace GodMode.Avalonia.ViewModels;

public partial class CredentialListViewModel : ViewModelBase
{
	private readonly ICredentialService _credentialService;

	public event Action? Completed;

	[ObservableProperty]
	private string _profileName = string.Empty;

	/// <summary>When true, credentials from all profiles are shown grouped.</summary>
	[ObservableProperty]
	private bool _isAllProfiles;

	/// <summary>All profile names to load from when IsAllProfiles is true.</summary>
	public List<string> AllProfileNames { get; set; } = [];

	[ObservableProperty]
	private ObservableCollection<CredentialItemViewModel> _credentials = new();

	[ObservableProperty]
	private bool _isLoading;

	[ObservableProperty]
	private bool _isEditing;

	[ObservableProperty]
	private string _editName = string.Empty;

	[ObservableProperty]
	private string _editType = "api-key";

	[ObservableProperty]
	private string _editValue = string.Empty;

	[ObservableProperty]
	private string? _editingExistingName;

	/// <summary>Profile name for the credential being edited.</summary>
	[ObservableProperty]
	private string _editProfileName = string.Empty;

	[ObservableProperty]
	private string? _errorMessage;

	public string[] CredentialTypes { get; } = ["api-key", "pat", "bearer-token", "aws-key"];

	public CredentialListViewModel(
		INavigationService navigationService,
		ICredentialService credentialService)
		: base(navigationService)
	{
		_credentialService = credentialService;
	}

	[RelayCommand]
	private async Task LoadAsync()
	{
		IsLoading = true;
		ErrorMessage = null;

		try
		{
			if (IsAllProfiles)
			{
				var allCreds = new List<CredentialItemViewModel>();
				foreach (var profile in AllProfileNames)
				{
					var creds = await _credentialService.ListCredentialsAsync(profile);
					allCreds.AddRange(creds.Select(c => new CredentialItemViewModel(c.Name, c.Type, profile)));
				}
				Credentials = new ObservableCollection<CredentialItemViewModel>(allCreds);
			}
			else
			{
				var creds = await _credentialService.ListCredentialsAsync(ProfileName);
				Credentials = new ObservableCollection<CredentialItemViewModel>(
					creds.Select(c => new CredentialItemViewModel(c.Name, c.Type, ProfileName)));
			}
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error loading credentials: {ex.Message}";
		}
		finally
		{
			IsLoading = false;
		}
	}

	[RelayCommand]
	private void StartAdd()
	{
		EditingExistingName = null;
		EditName = string.Empty;
		EditType = "api-key";
		EditValue = string.Empty;
		EditProfileName = IsAllProfiles ? (AllProfileNames.FirstOrDefault() ?? string.Empty) : ProfileName;
		ErrorMessage = null;
		IsEditing = true;
	}

	[RelayCommand]
	private void StartEdit(CredentialItemViewModel item)
	{
		EditingExistingName = item.Name;
		EditName = item.Name;
		EditType = item.Type;
		EditValue = string.Empty; // Don't show existing value
		EditProfileName = item.ProfileName;
		ErrorMessage = null;
		IsEditing = true;
	}

	[RelayCommand]
	private async Task SaveEditAsync()
	{
		ErrorMessage = null;

		if (string.IsNullOrWhiteSpace(EditName))
		{
			ErrorMessage = "Please enter a credential name";
			return;
		}

		if (string.IsNullOrWhiteSpace(EditValue))
		{
			ErrorMessage = "Please enter the credential value";
			return;
		}

		if (string.IsNullOrWhiteSpace(EditProfileName))
		{
			ErrorMessage = "Please select a profile";
			return;
		}

		try
		{
			// If renaming, delete the old one
			if (EditingExistingName != null && EditingExistingName != EditName)
				await _credentialService.DeleteCredentialAsync(EditProfileName, EditingExistingName);

			await _credentialService.SaveCredentialAsync(EditProfileName, EditName, EditType, EditValue);
			IsEditing = false;
			await LoadAsync();
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error saving credential: {ex.Message}";
		}
	}

	[RelayCommand]
	private void CancelEdit()
	{
		IsEditing = false;
		ErrorMessage = null;
	}

	[RelayCommand]
	private async Task DeleteAsync(CredentialItemViewModel item)
	{
		try
		{
			await _credentialService.DeleteCredentialAsync(item.ProfileName, item.Name);
			Credentials.Remove(item);
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error deleting credential: {ex.Message}";
		}
	}

	[RelayCommand]
	private void Close() => Completed?.Invoke();
}

public partial class CredentialItemViewModel : ObservableObject
{
	[ObservableProperty]
	private string _name;

	[ObservableProperty]
	private string _type;

	[ObservableProperty]
	private string _profileName;

	public CredentialItemViewModel(string name, string type, string profileName)
	{
		_name = name;
		_type = type;
		_profileName = profileName;
	}
}
