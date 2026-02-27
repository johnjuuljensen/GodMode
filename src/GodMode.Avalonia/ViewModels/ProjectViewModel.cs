using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.Shared.Models;
using GodMode.Shared.Enums;
using System.Collections.ObjectModel;

namespace GodMode.Avalonia.ViewModels;

public partial class ProjectViewModel : ViewModelBase, IDisposable
{
	private readonly IProjectService _projectService;
	private readonly INotificationService _notificationService;
	private IDisposable? _outputSubscription;

	[ObservableProperty]
	private string _profileName = string.Empty;

	[ObservableProperty]
	private string _hostId = string.Empty;

	[ObservableProperty]
	private string _projectId = string.Empty;

	[ObservableProperty]
	private ProjectStatus? _status;

	[ObservableProperty]
	private ObservableCollection<ClaudeMessage> _outputMessages = new();

	[ObservableProperty]
	private string _inputText = string.Empty;

	[ObservableProperty]
	private bool _isLoading;

	[ObservableProperty]
	private string? _errorMessage;

	[ObservableProperty]
	private bool _canSendInput;

	[ObservableProperty]
	private bool _canResume;

	[ObservableProperty]
	private bool _showMetrics;

	[ObservableProperty]
	private string? _metricsHtml;

	public ProjectViewModel(
		INavigationService navigationService,
		IProjectService projectService,
		INotificationService notificationService)
		: base(navigationService)
	{
		_projectService = projectService;
		_notificationService = notificationService;
	}

	partial void OnProfileNameChanged(string value)
	{
		if (!string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(HostId) && !string.IsNullOrEmpty(ProjectId))
			_ = LoadAsync();
	}

	partial void OnHostIdChanged(string value)
	{
		if (!string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(ProfileName) && !string.IsNullOrEmpty(ProjectId))
			_ = LoadAsync();
	}

	partial void OnProjectIdChanged(string value)
	{
		if (!string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(ProfileName) && !string.IsNullOrEmpty(HostId))
			_ = LoadAsync();
	}

	[RelayCommand]
	private async Task LoadAsync()
	{
		if (string.IsNullOrEmpty(ProfileName) || string.IsNullOrEmpty(HostId) || string.IsNullOrEmpty(ProjectId))
			return;

		IsLoading = true;
		ErrorMessage = null;

		try
		{
			Status = await _projectService.GetStatusAsync(ProfileName, HostId, ProjectId);
			await SubscribeToOutputAsync();
			UpdateCanSendInput();
			_notificationService.ClearBadgeCountForProject(ProfileName, HostId, ProjectId);
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error loading project: {ex.Message}";
		}
		finally
		{
			IsLoading = false;
		}
	}

	[RelayCommand]
	private async Task RefreshAsync()
	{
		try
		{
			Status = await _projectService.GetStatusAsync(ProfileName, HostId, ProjectId, forceRefresh: true);
			UpdateCanSendInput();
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error refreshing status: {ex.Message}";
		}
	}

	[RelayCommand]
	private async Task SendInputAsync()
	{
		if (string.IsNullOrWhiteSpace(InputText) || !CanSendInput)
			return;

		var input = InputText;
		InputText = string.Empty;

		try
		{
			await _projectService.SendInputAsync(ProfileName, HostId, ProjectId, input);

			var userJson = System.Text.Json.JsonSerializer.Serialize(new
			{
				type = "user",
				message = new
				{
					role = "user",
					content = new[] { new { type = "text", text = input } }
				}
			});
			OutputMessages.Add(new ClaudeMessage(userJson));

			await RefreshAsync();
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error sending input: {ex.Message}";
			InputText = input;
		}
	}

	[RelayCommand]
	private async Task StopProjectAsync()
	{
		try
		{
			IsLoading = true;
			await _projectService.StopProjectAsync(ProfileName, HostId, ProjectId);
			await RefreshAsync();
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error stopping project: {ex.Message}";
		}
		finally
		{
			IsLoading = false;
		}
	}

	[RelayCommand]
	private async Task ResumeProjectAsync()
	{
		try
		{
			IsLoading = true;
			ErrorMessage = null;
			await _projectService.ResumeProjectAsync(ProfileName, HostId, ProjectId);
			await RefreshAsync();
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error resuming project: {ex.Message}";
		}
		finally
		{
			IsLoading = false;
		}
	}

	[RelayCommand]
	private async Task DeleteProjectAsync()
	{
		try
		{
			IsLoading = true;
			ErrorMessage = null;
			await _projectService.DeleteProjectAsync(ProfileName, HostId, ProjectId);
			Navigation.GoBack();
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error deleting project: {ex.Message}";
		}
		finally
		{
			IsLoading = false;
		}
	}

	[RelayCommand]
	private async Task LoadMetricsAsync()
	{
		try
		{
			IsLoading = true;
			MetricsHtml = await _projectService.GetMetricsHtmlAsync(ProfileName, HostId, ProjectId);
			ShowMetrics = true;
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error loading metrics: {ex.Message}";
		}
		finally
		{
			IsLoading = false;
		}
	}

	[RelayCommand]
	private void GoBack() => Navigation.GoBack();

	[RelayCommand]
	private void CloseMetrics() => ShowMetrics = false;

	[RelayCommand]
	private void ToggleMessageExpanded(ClaudeMessage message) => message.IsExpanded = !message.IsExpanded;

	partial void OnStatusChanged(ProjectStatus? value) => UpdateCanSendInput();

	private void UpdateCanSendInput()
	{
		CanSendInput = Status?.State is ProjectState.WaitingInput or ProjectState.Running;
		CanResume = Status?.State is ProjectState.Stopped or ProjectState.Idle;
	}

	private async Task SubscribeToOutputAsync()
	{
		_outputSubscription?.Dispose();

		try
		{
			OutputMessages.Clear();

			var observable = await _projectService.SubscribeOutputAsync(ProfileName, HostId, ProjectId, fromOffset: 0);

			_outputSubscription = observable
				.Subscribe(
					onNext: message =>
					{
						Dispatcher.UIThread.Post(() => OutputMessages.Add(message));
					},
					onError: error =>
					{
						Dispatcher.UIThread.Post(() => ErrorMessage = $"Output stream error: {error.Message}");
					}
				);
		}
		catch (Exception ex)
		{
			ErrorMessage = $"Error subscribing to output: {ex.Message}";
		}
	}

	public void Dispose()
	{
		_outputSubscription?.Dispose();
	}
}
