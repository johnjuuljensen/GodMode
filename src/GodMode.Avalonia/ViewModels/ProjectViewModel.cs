using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.Avalonia.Models;
using GodMode.Shared.Models;
using GodMode.Shared.Enums;
using System.Collections.ObjectModel;

namespace GodMode.Avalonia.ViewModels;

public partial class ProjectViewModel : ViewModelBase, IDisposable
{
	private const int DisplayMessageWindow = 50;
	private const int LoadMoreBatch = 50;

	private readonly IProjectService _projectService;
	private readonly INotificationService _notificationService;
	private readonly IDialogService _dialogService;
	private IDisposable? _outputSubscription;
	private bool _hasLoaded;
	private DateTime _lastInputSentAt;
	private int _displayStartIndex;
	private bool _displayDirty;
	private DispatcherTimer? _displayFlushTimer;
	private string? _lastQuestionMessageId;
	private bool _isReplaying;

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
	private bool _isOptionPickerDismissed;

	[ObservableProperty]
	private string? _currentQuestionText;

	[ObservableProperty]
	private IReadOnlyList<QuestionOptionData> _currentQuestionOptions = [];

	[ObservableProperty]
	private string? _currentQuestionHeader;

	[ObservableProperty]
	private string _inputWatermark = "Type your response...";

	[ObservableProperty]
	private bool _isSimpleView;

	[ObservableProperty]
	private ObservableCollection<ChatDisplayItem> _displayMessages = new();

	[ObservableProperty]
	private bool _showMetrics;

	[ObservableProperty]
	private string? _metricsHtml;

	[ObservableProperty]
	private bool _hasEarlierMessages;

	[ObservableProperty]
	private string _earlierMessagesText = string.Empty;

	/// <summary>
	/// Fires when project status changes. Args: projectId, state, currentQuestion.
	/// </summary>
	public event Action<string, ProjectState, string?>? ProjectStatusUpdated;

	public ProjectViewModel(
		INavigationService navigationService,
		IProjectService projectService,
		INotificationService notificationService,
		IDialogService dialogService)
		: base(navigationService)
	{
		_projectService = projectService;
		_notificationService = notificationService;
		_dialogService = dialogService;
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

	/// <summary>
	/// Dismisses the option picker but keeps the session in Awaiting Input state.
	/// The user can still type a free-text response.
	/// </summary>
	public void DismissQuestion()
	{
		IsOptionPickerDismissed = true;
		UpdateCanSendInput();
	}

	/// <summary>
	/// Fully dismisses the question — clears all question state so the session
	/// no longer shows as waiting for input.
	/// </summary>
	public void FullyDismissQuestion()
	{
		IsQuestionActive = false;
		IsOptionPickerDismissed = false;
		CurrentQuestionText = null;
		CurrentQuestionOptions = [];
		CurrentQuestionHeader = null;
		_lastQuestionMessageId = null;
		UpdateCanSendInput();
	}

	/// <summary>
	/// Called when the user confirms an option selection.
	/// Clears question state and unlocks input so SendInput can proceed.
	/// </summary>
	public void AcceptOptionSelection()
	{
		IsQuestionActive = false;
		IsOptionPickerDismissed = false;
		CurrentQuestionText = null;
		CurrentQuestionOptions = [];
		CurrentQuestionHeader = null;
		_lastQuestionMessageId = null;
		UpdateCanSendInput();
	}

	/// <summary>
	/// Called when the server pushes a status change via SignalR.
	/// </summary>
	public void OnServerStatusPush(ProjectStatus status)
	{
		System.Diagnostics.Debug.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] StatusPush: {ProjectId} → {status.State} (question: {status.CurrentQuestion?.Substring(0, Math.Min(60, status.CurrentQuestion?.Length ?? 0))})");
		Status = status;
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
		IsOptionPickerDismissed = false;
		CurrentQuestionText = null;
		CurrentQuestionOptions = [];
		CurrentQuestionHeader = null;
		_lastQuestionMessageId = null;

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
			var userMessage = new ClaudeMessage(userJson);
			OutputMessages.Add(userMessage);
			FlushDisplayMessages();

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
		var (confirmed, force) = await _dialogService.ConfirmDeleteAsync(Status?.Name ?? ProjectId);
		if (!confirmed) return;

		try
		{
			IsLoading = true;
			ErrorMessage = null;
			await _projectService.DeleteProjectAsync(ProfileName, HostId, ProjectId, force);
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
	private void ToggleSimpleView() => IsSimpleView = !IsSimpleView;

	partial void OnIsSimpleViewChanged(bool value) => RebuildDisplayMessages();

	[RelayCommand]
	private void ToggleMessageExpanded(ClaudeMessage message) => message.IsExpanded = !message.IsExpanded;

	partial void OnStatusChanged(ProjectStatus? value)
	{
		UpdateCanSendInput();
		DetectQuestionFromStatus();

		// Propagate state changes to sidebar/tile views.
		// Client-side question detection overrides server state for sidebar display.
		if (value != null && !string.IsNullOrEmpty(ProjectId))
		{
			var effectiveState = IsQuestionActive ? ProjectState.WaitingInput : value.State;
			var effectiveQuestion = IsQuestionActive ? CurrentQuestionText : value.CurrentQuestion;
			ProjectStatusUpdated?.Invoke(ProjectId, effectiveState, effectiveQuestion);
		}
	}

	partial void OnIsQuestionActiveChanged(bool value)
	{
		UpdateCanSendInput();

		// Notify sidebar/tiles so waiting indicators update from client-side detection
		if (!string.IsNullOrEmpty(ProjectId))
		{
			var state = value ? ProjectState.WaitingInput : (Status?.State ?? ProjectState.Running);
			ProjectStatusUpdated?.Invoke(ProjectId, state, value ? CurrentQuestionText : null);
		}
	}
	partial void OnIsOptionPickerDismissedChanged(bool value) => UpdateCanSendInput();

	private void UpdateCanSendInput()
	{
		// When question is active with options and picker is NOT dismissed, lock the input (IC-03)
		// Open-ended questions (no options) should not lock the input
		var inputLocked = IsQuestionActive && !IsOptionPickerDismissed && CurrentQuestionOptions.Count > 0;

		// Allow sending when picker is dismissed (free-text mode), or normal states
		CanSendInput = !inputLocked && (
			IsQuestionActive // picker dismissed but question still active
			|| Status?.State is ProjectState.WaitingInput or ProjectState.Running
			|| Status?.State is ProjectState.Stopped or ProjectState.Idle);
		CanResume = !IsQuestionActive && Status?.State is ProjectState.Stopped or ProjectState.Idle;
		InputWatermark = inputLocked ? "Select an option above" :
			CanResume ? "Type to resume..." : "Type your response...";
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
			IsOptionPickerDismissed = false;
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

		// Lettered list: a. / b. / a) / b) style
		var letteredItems = System.Text.RegularExpressions.Regex.Matches(trimmed, @"^\s*[a-z][.)]\s+", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		if (letteredItems.Count >= 2) return true;

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

		// Lettered list: a. Option / b. Option or a) Option / b) Option
		var lettered = System.Text.RegularExpressions.Regex.Matches(trimmed, @"^\s*[a-z][.)]\s+(.+)$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		if (lettered.Count >= 2)
			return lettered.Cast<System.Text.RegularExpressions.Match>()
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

	/// <summary>
	/// Rebuilds the display messages collection from the source output messages,
	/// applying simple/detailed view filtering and the display window.
	/// </summary>
	private void RebuildDisplayMessages()
	{
		DisplayMessages.Clear();
		_displayStartIndex = Math.Max(0, OutputMessages.Count - DisplayMessageWindow);

		if (!IsSimpleView)
		{
			for (int i = _displayStartIndex; i < OutputMessages.Count; i++)
				DisplayMessages.Add(new ChatDisplayItem(OutputMessages[i]));
			UpdateEarlierMessagesState();
			return;
		}

		// Simple view: collapse consecutive tool-only messages into summaries
		int consecutiveToolCount = 0;
		for (int i = _displayStartIndex; i < OutputMessages.Count; i++)
		{
			var msg = OutputMessages[i];
			if (msg.IsToolOnly)
			{
				consecutiveToolCount++;
				continue;
			}

			if (consecutiveToolCount > 0)
			{
				DisplayMessages.Add(new ChatDisplayItem(consecutiveToolCount));
				consecutiveToolCount = 0;
			}

			DisplayMessages.Add(new ChatDisplayItem(msg, isSimpleView: true));
		}

		if (consecutiveToolCount > 0)
			DisplayMessages.Add(new ChatDisplayItem(consecutiveToolCount));

		UpdateEarlierMessagesState();
	}

	private void UpdateEarlierMessagesState()
	{
		HasEarlierMessages = _displayStartIndex > 0;
		EarlierMessagesText = HasEarlierMessages
			? $"Load {Math.Min(LoadMoreBatch, _displayStartIndex)} earlier messages ({_displayStartIndex} hidden)"
			: string.Empty;
	}

	[RelayCommand]
	private void LoadEarlierMessages()
	{
		if (_displayStartIndex <= 0) return;

		var loadCount = Math.Min(LoadMoreBatch, _displayStartIndex);
		var newStart = _displayStartIndex - loadCount;

		// Build the earlier items to prepend
		var earlier = new List<ChatDisplayItem>();
		if (!IsSimpleView)
		{
			for (int i = newStart; i < _displayStartIndex; i++)
				earlier.Add(new ChatDisplayItem(OutputMessages[i]));
		}
		else
		{
			int consecutiveToolCount = 0;
			for (int i = newStart; i < _displayStartIndex; i++)
			{
				var msg = OutputMessages[i];
				if (msg.IsToolOnly)
				{
					consecutiveToolCount++;
					continue;
				}

				if (consecutiveToolCount > 0)
				{
					earlier.Add(new ChatDisplayItem(consecutiveToolCount));
					consecutiveToolCount = 0;
				}

				earlier.Add(new ChatDisplayItem(msg, isSimpleView: true));
			}

			// Merge trailing tool summary with the first existing display item if applicable
			if (consecutiveToolCount > 0)
			{
				if (DisplayMessages.Count > 0 && DisplayMessages[0].IsToolSummary)
					DisplayMessages[0].ToolCallCount += consecutiveToolCount;
				else
					earlier.Add(new ChatDisplayItem(consecutiveToolCount));
			}
		}

		// Prepend: insert in reverse order at index 0
		for (int i = earlier.Count - 1; i >= 0; i--)
			DisplayMessages.Insert(0, earlier[i]);

		_displayStartIndex = newStart;
		UpdateEarlierMessagesState();
	}

	private async Task SubscribeToOutputAsync()
	{
		_outputSubscription?.Dispose();

		try
		{
			OutputMessages.Clear();
			DisplayMessages.Clear();
			_displayDirty = false;

			// Debounce timer: when messages stop arriving, flush the tail to display
			_displayFlushTimer?.Stop();
			_displayFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
			_displayFlushTimer.Tick += (_, _) =>
			{
				_displayFlushTimer.Stop();
				if (_displayDirty)
					FlushDisplayMessages();
			};

			_isReplaying = true;
			var observable = await _projectService.SubscribeOutputAsync(ProfileName, HostId, ProjectId, fromOffset: 0);

			_outputSubscription = observable
				.Subscribe(
					onNext: message =>
					{
						Dispatcher.UIThread.Post(() =>
						{
							OutputMessages.Add(message);

							// Mark dirty and restart debounce timer (don't touch DisplayMessages yet)
							_displayDirty = true;
							_displayFlushTimer.Stop();
							_displayFlushTimer.Start();

							// During replay, skip per-message detection to avoid flickering.
							// Question state is detected once after flush in DetectQuestionFromLastMessage.
							if (!_isReplaying)
								DetectQuestionFromMessage(message);
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

	/// <summary>
	/// Flushes the display: rebuilds from the tail of OutputMessages in a single pass.
	/// </summary>
	private void FlushDisplayMessages()
	{
		var wasReplaying = _isReplaying;
		_isReplaying = false;
		_displayDirty = false;
		RebuildDisplayMessages();

		// After replay ends (or any flush), detect question from the final message.
		// During replay, per-message detection is skipped to avoid flickering from
		// result messages in earlier turns toggling IsQuestionActive on and off.
		if (!IsQuestionActive)
			DetectQuestionFromLastMessage();
	}

	/// <summary>
	/// Checks the last message in OutputMessages for a pending question.
	/// Used as a post-replay safety net.
	/// </summary>
	private void DetectQuestionFromLastMessage()
	{
		if (OutputMessages.Count == 0) return;

		var lastMsg = OutputMessages[^1];

		// Check structured question
		if (lastMsg.IsQuestion && lastMsg.QuestionOptions.Count > 0)
		{
			_lastQuestionMessageId = lastMsg.QuestionText;
			IsOptionPickerDismissed = false;
			IsQuestionActive = true;
			CurrentQuestionText = lastMsg.QuestionText;
			CurrentQuestionOptions = lastMsg.QuestionOptions;
			CurrentQuestionHeader = lastMsg.QuestionHeader;
			return;
		}

		// Check heuristic text question
		if (lastMsg.Type == "assistant" && !string.IsNullOrEmpty(lastMsg.ContentSummary)
			&& LooksLikeQuestion(lastMsg.ContentSummary))
		{
			_lastQuestionMessageId = lastMsg.ContentSummary;
			IsOptionPickerDismissed = false;
			IsQuestionActive = true;
			CurrentQuestionText = lastMsg.ContentSummary;
			CurrentQuestionHeader = Status?.Name;
			CurrentQuestionOptions = ParseTextOptions(lastMsg.ContentSummary);
		}
	}

	/// <summary>
	/// Detects questions from a newly received message (layers 1-3).
	/// </summary>
	private void DetectQuestionFromMessage(ClaudeMessage message)
	{
		// EC-03: Duplicate question detection — skip if same question text received again
		var questionKey = message.IsQuestion ? message.QuestionText : message.ContentSummary;
		if (!string.IsNullOrEmpty(questionKey) && questionKey == _lastQuestionMessageId)
			return;

		// Layer 1: Structured AskUserQuestion tool_use
		if (message.IsQuestion && message.QuestionOptions.Count > 0)
		{
			_lastQuestionMessageId = questionKey;
			IsOptionPickerDismissed = false;
			IsQuestionActive = true;
			CurrentQuestionText = message.QuestionText;
			CurrentQuestionOptions = message.QuestionOptions;
			CurrentQuestionHeader = message.QuestionHeader;
			System.Diagnostics.Debug.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] Question detected (structured): {ProjectId}");
		}
		// Layer 2: Text-based question from assistant message
		else if (message.Type == "assistant" && !string.IsNullOrEmpty(message.ContentSummary)
			&& LooksLikeQuestion(message.ContentSummary))
		{
			_lastQuestionMessageId = questionKey;
			IsOptionPickerDismissed = false;
			IsQuestionActive = true;
			CurrentQuestionText = message.ContentSummary;
			CurrentQuestionHeader = Status?.Name;
			CurrentQuestionOptions = ParseTextOptions(message.ContentSummary);
			System.Diagnostics.Debug.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] Question detected (heuristic): {ProjectId}");
		}
		// Layer 3: Result means turn is over — clear question if active,
		// then refresh status to check for WaitingInput
		else if (message.Type == "result")
		{
			if (IsQuestionActive)
			{
				IsQuestionActive = false;
				IsOptionPickerDismissed = false;
				CurrentQuestionText = null;
				CurrentQuestionOptions = [];
				CurrentQuestionHeader = null;
				_lastQuestionMessageId = null;
				System.Diagnostics.Debug.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] Question cleared (result): {ProjectId}");
			}
			_ = RefreshAsync();
		}
	}

	public void Dispose()
	{
		_displayFlushTimer?.Stop();
		_outputSubscription?.Dispose();
	}
}
