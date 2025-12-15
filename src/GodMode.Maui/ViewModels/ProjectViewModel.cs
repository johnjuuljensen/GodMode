using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.Maui.Services;
using GodMode.Shared.Models;
using GodMode.Shared.Enums;
using System.Collections.ObjectModel;

namespace GodMode.Maui.ViewModels;

/// <summary>
/// ViewModel for the project detail page with chat interface and status board
/// </summary>
[QueryProperty(nameof(ProfileName), "profileName")]
[QueryProperty(nameof(HostId), "hostId")]
[QueryProperty(nameof(ProjectId), "projectId")]
public partial class ProjectViewModel : ObservableObject, IDisposable
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

    [ObservableProperty]
    private bool _isSimpleMode = true;

    public ProjectViewModel(
        IProjectService projectService,
        INotificationService notificationService)
    {
        _projectService = projectService;
        _notificationService = notificationService;
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
            // Load project status
            Status = await _projectService.GetStatusAsync(ProfileName, HostId, ProjectId);

            // Subscribe to output events
            await SubscribeToOutputAsync();

            // Update input capability
            UpdateCanSendInput();

            // Clear badge
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

            // Add to output immediately for UI feedback
            // Create a simple user message JSON
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

            // Refresh status
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error sending input: {ex.Message}";
            InputText = input; // Restore the input
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
    private void CloseMetrics()
    {
        ShowMetrics = false;
    }

    [RelayCommand]
    private void ToggleViewMode()
    {
        IsSimpleMode = !IsSimpleMode;
    }

    [RelayCommand]
    private void ScrollToBottom()
    {
        // This would be handled by the view
    }

    partial void OnStatusChanged(ProjectStatus? value)
    {
        UpdateCanSendInput();
    }

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
            var outputOffset = Status?.OutputOffset ?? 0;
            var observable = await _projectService.SubscribeOutputAsync(ProfileName, HostId, ProjectId, outputOffset);

            _outputSubscription = observable.Subscribe(
                onNext: message =>
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        OutputMessages.Add(message);

                        // Auto-scroll would be handled by the view
                    });
                },
                onError: error =>
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        ErrorMessage = $"Output stream error: {error.Message}";
                    });
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
