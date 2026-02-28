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
	private bool _hasLoaded;
	private DateTime _lastInputSentAt;

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
	private bool _isQuestionActive;

	[ObservableProperty]
	private string? _currentQuestionText;

	[ObservableProperty]
	private IReadOnlyList<QuestionOptionData> _currentQuestionOptions = [];

	[ObservableProperty]
	private string? _currentQuestionHeader;

	[ObservableProperty]
	private string _inputWatermark = "Type your response...";

	[ObservableProperty]
	private bool _showMetrics;

	[ObservableProperty]
	private string? _metricsHtml;

	/// <summary>
	/// Fires when project status changes. Args: projectId, state, currentQuestion.
	/// </summary>
	public event Action<string, ProjectState, string?>? ProjectStatusUpdated;

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

			if (!_hasLoaded)
			{
				await SubscribeToOutputAsync();
				_hasLoaded = true;
			}

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
		_lastInputSentAt = DateTime.UtcNow;
		IsQuestionActive = false;
		CurrentQuestionText = null;
		CurrentQuestionOptions = [];
		CurrentQuestionHeader = null;

		try
		{
			// Auto-resume if project is stopped/idle
			if (Status?.State is ProjectState.Stopped or ProjectState.Idle)
			{
				IsLoading = true;
				ErrorMessage = null;
				await _projectService.ResumeProjectAsync(ProfileName, HostId, ProjectId);
				IsLoading = false;

				// Wait briefly for process to be ready to accept input
				await Task.Delay(500);
			}

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
			IsLoading = false;
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

	partial void OnStatusChanged(ProjectStatus? value)
	{
		UpdateCanSendInput();
		DetectQuestionFromStatus();

		// Propagate state changes to sidebar/tile views
		if (value != null && !string.IsNullOrEmpty(ProjectId))
			ProjectStatusUpdated?.Invoke(ProjectId, value.State, value.CurrentQuestion);
	}

	partial void OnIsQuestionActiveChanged(bool value) => UpdateCanSendInput();

	private void UpdateCanSendInput()
	{
		// Always allow sending: if active → sends input; if stopped/idle → auto-resumes with input
		CanSendInput = IsQuestionActive
			|| Status?.State is ProjectState.WaitingInput or ProjectState.Running
			|| Status?.State is ProjectState.Stopped or ProjectState.Idle;
		CanResume = !IsQuestionActive && Status?.State is ProjectState.Stopped or ProjectState.Idle;
		InputWatermark = CanResume ? "Type to resume..." : "Type your response...";
	}

	/// <summary>
	/// Detects questions from project status (server-side detection) and
	/// from the last assistant message text (client-side heuristic).
	/// </summary>
	private void DetectQuestionFromStatus()
	{
		// Don't override an active structured question (AskUserQuestion)
		if (IsQuestionActive && CurrentQuestionOptions.Count > 0)
			return;

		// Don't re-detect a question right after the user just answered one
		if ((DateTime.UtcNow - _lastInputSentAt).TotalSeconds < 5)
			return;

		var isWaiting = Status?.State is ProjectState.WaitingInput or ProjectState.Idle;
		if (!isWaiting) return;

		// Try server-side CurrentQuestion first
		var questionText = Status?.CurrentQuestion;

		// Client-side fallback: check the last assistant message
		if (string.IsNullOrEmpty(questionText))
		{
			questionText = GetLastAssistantQuestion();
		}

		if (!string.IsNullOrEmpty(questionText))
		{
			IsQuestionActive = true;
			CurrentQuestionText = questionText;
			CurrentQuestionHeader = Status?.Name;
			CurrentQuestionOptions = ParseTextOptions(questionText);
		}
	}

	/// <summary>
	/// Extracts the last assistant message text if it looks like a question.
	/// </summary>
	private string? GetLastAssistantQuestion()
	{
		for (int i = OutputMessages.Count - 1; i >= 0; i--)
		{
			var msg = OutputMessages[i];
			if (msg.Type == "user") break; // Don't look past the last user message
			if (msg.Type != "assistant") continue;

			var text = msg.ContentSummary;
			if (!string.IsNullOrEmpty(text) && LooksLikeQuestion(text))
				return text;
		}
		return null;
	}

	/// <summary>
	/// Client-side heuristic to detect if text is a question.
	/// </summary>
	private static bool LooksLikeQuestion(string text)
	{
		var trimmed = text.Trim();

		// Ends with question mark
		if (trimmed.EndsWith('?')) return true;

		// (y/n) or (yes/no) style
		if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"\(y(?:es)?/n(?:o)?\)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
			return true;

		// (a/b/c) style options
		if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"\([^)]+/[^)]+\)\s*$"))
			return true;

		// Numbered list with 2+ items suggests choices
		var numberedItems = System.Text.RegularExpressions.Regex.Matches(trimmed, @"^\s*\d+[.)]\s+", System.Text.RegularExpressions.RegexOptions.Multiline);
		if (numberedItems.Count >= 2) return true;

		// Common question phrases
		var phrases = new[] { "would you like", "do you want", "should i", "shall i", "which one", "please choose", "please select" };
		var lower = trimmed.ToLowerInvariant();
		foreach (var phrase in phrases)
		{
			if (lower.Contains(phrase)) return true;
		}

		return false;
	}

	/// <summary>
	/// Parses inline options from question text (y/n, numbered lists, etc.)
	/// </summary>
	private static IReadOnlyList<QuestionOptionData> ParseTextOptions(string text)
	{
		var trimmed = text.Trim();

		// (y/n) or (yes/no)
		if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"\(y(?:es)?/n(?:o)?\)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
			return [new("Yes", null), new("No", null)];

		// (a/b/c) style
		var parenMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"\(([^)]+/[^)]+)\)\s*$");
		if (parenMatch.Success)
		{
			var parts = parenMatch.Groups[1].Value.Split('/');
			return parts.Select(p => new QuestionOptionData(p.Trim(), null)).ToList();
		}

		// Numbered list: 1. Option one\n2. Option two
		var numbered = System.Text.RegularExpressions.Regex.Matches(trimmed, @"^\s*\d+[.)]\s+(.+)$", System.Text.RegularExpressions.RegexOptions.Multiline);
		if (numbered.Count >= 2)
			return numbered.Cast<System.Text.RegularExpressions.Match>()
				.Select(m => new QuestionOptionData(m.Groups[1].Value.Trim(), null))
				.ToList();

		// Bullet list: - Option one\n- Option two
		var bullets = System.Text.RegularExpressions.Regex.Matches(trimmed, @"^\s*[-*]\s+(.+)$", System.Text.RegularExpressions.RegexOptions.Multiline);
		if (bullets.Count >= 2)
			return bullets.Cast<System.Text.RegularExpressions.Match>()
				.Select(m => new QuestionOptionData(m.Groups[1].Value.Trim(), null))
				.ToList();

		return [];
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
						Dispatcher.UIThread.Post(() =>
						{
							OutputMessages.Add(message);

							// Layer 1: Structured AskUserQuestion tool_use
							if (message.IsQuestion && message.QuestionOptions.Count > 0)
							{
								IsQuestionActive = true;
								CurrentQuestionText = message.QuestionText;
								CurrentQuestionOptions = message.QuestionOptions;
								CurrentQuestionHeader = message.QuestionHeader;
							}
							// Layer 2: Text-based question from assistant message
							else if (message.Type == "assistant" && !string.IsNullOrEmpty(message.ContentSummary)
								&& LooksLikeQuestion(message.ContentSummary))
							{
								IsQuestionActive = true;
								CurrentQuestionText = message.ContentSummary;
								CurrentQuestionHeader = Status?.Name;
								CurrentQuestionOptions = ParseTextOptions(message.ContentSummary);
							}
							// Layer 3: Result means turn is over — clear question if active,
							// then refresh status to check for WaitingInput
							else if (message.Type == "result")
							{
								if (IsQuestionActive)
								{
									IsQuestionActive = false;
									CurrentQuestionText = null;
									CurrentQuestionOptions = [];
									CurrentQuestionHeader = null;
								}
								_ = RefreshAsync();
							}
						});
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
