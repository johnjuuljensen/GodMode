using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.Avalonia.Voice;
using GodMode.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace GodMode.Avalonia.ViewModels;

public partial class VoiceAssistantViewModel : ViewModelBase
{
	private readonly AssistantService _assistant;
	private readonly InferenceRouter _router;
	private readonly VoiceContext _voiceContext;
	private readonly VoiceConfig _voiceConfig;
	private readonly ISpeechRecognizer _recognizer;
	private CancellationTokenSource? _cts;

	private static readonly string[] UnfocusKeywords =
		["unfocus", "exit focus", "stop watching", "leave project"];

	[ObservableProperty]
	private string _statusText = "Initializing...";

	[ObservableProperty]
	private string _inputText = string.Empty;

	[ObservableProperty]
	private bool _isBusy;

	[ObservableProperty]
	private bool _isModelLoaded;

	[ObservableProperty]
	private string _inferenceStatusText = "No inference configured";

	[ObservableProperty]
	private ObservableCollection<string> _speechLanguages = new();

	[ObservableProperty]
	private string? _selectedLanguage;

	[ObservableProperty]
	private string _sttEngineName = string.Empty;

	[ObservableProperty]
	private string _configPath = string.Empty;

	public ObservableCollection<VoiceMessage> Messages { get; } = new();

	public VoiceAssistantViewModel(
		INavigationService navigationService,
		AssistantService assistant,
		InferenceRouter router,
		ISpeechRecognizer recognizer,
		VoiceContext voiceContext)
		: base(navigationService)
	{
		_assistant = assistant;
		_router = router;
		_recognizer = recognizer;
		_voiceContext = voiceContext;
		_voiceConfig = VoiceConfig.Load();

		SttEngineName = $"STT: {_recognizer.EngineName}";
		ConfigPath = $"Config: {AIConfig.ConfigPath}";

		PopulateLanguages();

		// Wire voice context into AssistantService
		_assistant.ContextSummaryProvider = () => _voiceContext.GetSummary();
		_assistant.DisambiguationResolver = TryResolveDisambiguation;

		// Subscribe to assistant events
		_assistant.StatusChanged += (_, status) =>
			Dispatcher.UIThread.Post(() => StatusText = status);

		_assistant.TranscriptUpdated += (_, text) =>
			Dispatcher.UIThread.Post(() => AddMessage("You", text, isUser: true));

		_assistant.ResponseReceived += (_, text) =>
			Dispatcher.UIThread.Post(() => AddMessage("Assistant", text, isUser: false));

		_assistant.ErrorOccurred += (_, error) =>
			Dispatcher.UIThread.Post(() => AddMessage("Error", error, isUser: false, isError: true));

		// Subscribe to router status changes
		_router.StatusChanged += (_, _) =>
			Dispatcher.UIThread.Post(UpdateInferenceStatus);

		// Subscribe to focused project output
		_voiceContext.ProjectOutputReceived += OnProjectOutputReceived;

		// Check if models were already loaded at startup
		if (_router.IsLoaded)
		{
			IsModelLoaded = true;
			UpdateInferenceStatus();
			StatusText = "Models loaded.";
		}
		else
		{
			StatusText = "Models loading in background...";
			_ = WaitForModelLoadAsync();
		}
	}

	private void UpdateInferenceStatus()
	{
		IsModelLoaded = _router.IsLoaded;

		var tierMap = _router.TierProviderMap;
		if (tierMap.Count == 0 || tierMap.Values.All(p => p == "none"))
		{
			InferenceStatusText = "No inference configured";
			return;
		}

		// Group tiers by provider for compact display (e.g. "NPU: light | GPU: medium, heavy")
		var providerTiers = tierMap
			.Where(kv => kv.Value != "none")
			.GroupBy(kv => kv.Value)
			.Select(g =>
			{
				var providerLabel = g.Key.ToUpperInvariant();
				var tiers = string.Join(", ", g.Select(kv => kv.Key.ToString().ToLowerInvariant()));
				var status = _router.ProviderStatus.TryGetValue(g.Key, out var s) ? s : "unknown";
				return $"{providerLabel}: {tiers} ({status})";
			});

		InferenceStatusText = string.Join(" | ", providerTiers);
	}

	private async Task WaitForModelLoadAsync()
	{
		// Wait for the startup background load to complete
		while (!_router.IsLoaded)
		{
			await Task.Delay(500);
			// Check if router finished but nothing loaded (no config)
			if (_router.TierProviderMap.Count > 0 && _router.TierProviderMap.Values.All(p => p == "none"))
			{
				await Dispatcher.UIThread.InvokeAsync(() =>
				{
					UpdateInferenceStatus();
					StatusText = "No models configured.";
					ShowFirstRunMessage();
				});
				return;
			}
		}

		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			UpdateInferenceStatus();
			StatusText = "Models loaded.";
			AddMessage("System", "Models loaded automatically from config.", isUser: false);
		});
	}

	private ToolCall? TryResolveDisambiguation(string userText)
	{
		var pending = _voiceContext.PendingDisambiguation;
		if (pending is null) return null;

		var trimmed = userText.Trim();
		if (!int.TryParse(trimmed, out var choice) || choice < 1 || choice > pending.Options.Count)
		{
			// Not a number selection — clear disambiguation so LLM processes normally
			_voiceContext.ClearPendingDisambiguation();
			return null;
		}

		var selected = pending.Options[choice - 1];
		_voiceContext.ClearPendingDisambiguation();

		// Rebuild the original tool call with the resolved project name
		var newArgs = new Dictionary<string, object>(pending.OriginalArgs)
		{
			[pending.ProjectParamName] = selected.Summary.Name
		};

		return new ToolCall { ToolName = pending.OriginalToolName, Arguments = newArgs };
	}

	private void OnProjectOutputReceived(object? sender, ProjectOutputEventArgs e)
	{
		Dispatcher.UIThread.Post(() =>
		{
			var summary = SummarizeProjectOutput(e.Message);
			if (!string.IsNullOrWhiteSpace(summary))
				AddMessage(e.ProjectName, summary, isUser: false);
		});
	}

	private static string SummarizeProjectOutput(ClaudeMessage message)
	{
		if (!string.IsNullOrEmpty(message.ContentSummary))
			return message.ContentSummary;
		if (!string.IsNullOrEmpty(message.Summary))
			return message.Summary;
		return string.Empty;
	}

	private void PopulateLanguages()
	{
		List<string> languages;
		try
		{
			languages = _recognizer.GetAvailableLanguages().ToList();
		}
		catch
		{
			languages = ["en-US"];
		}

		SpeechLanguages = new ObservableCollection<string>(languages);

		var configured = _voiceConfig.SpeechLanguage;
		var match = languages.FirstOrDefault(l =>
			l.Equals(configured, StringComparison.OrdinalIgnoreCase));

		SelectedLanguage = match ?? languages.FirstOrDefault();
	}

	partial void OnSelectedLanguageChanged(string? value)
	{
		if (value is not null && value != _voiceConfig.SpeechLanguage)
		{
			_voiceConfig.SpeechLanguage = value;
			_voiceConfig.Save();
		}
	}

	private void ShowFirstRunMessage()
	{
		StatusText = "First run — configure inference in config file.";
		AddMessage("System",
			$"Welcome to GodMode Voice Assistant!\n\n" +
			$"No inference provider configured. To get started:\n\n" +
			$"Option 1 — Anthropic API (recommended):\n" +
			$"  Set ANTHROPIC_API_KEY env var, or add to config:\n" +
			$"  \"api_key\": \"sk-ant-...\", \"provider\": \"anthropic\"\n\n" +
			$"Option 2 — Local ONNX model:\n" +
			$"  Run: scripts/download-models.ps1\n" +
			$"  Set \"provider\": \"directml\" in config\n\n" +
			$"Config file: {AIConfig.ConfigPath}",
			isUser: false);
	}

	[RelayCommand]
	private void OpenConfig()
	{
		try
		{
			var path = AIConfig.ConfigPath;
			if (!File.Exists(path))
			{
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				File.WriteAllText(path, "{}");
			}
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
		}
		catch (Exception ex)
		{
			AddMessage("Error", $"Failed to open config: {ex.Message}", isUser: false, isError: true);
		}
	}

	private bool TryHandleUnfocusKeyword(string text)
	{
		var trimmed = text.Trim();
		if (UnfocusKeywords.Any(k => trimmed.Equals(k, StringComparison.OrdinalIgnoreCase)))
		{
			var name = _voiceContext.Focus?.ProjectName ?? "project";
			_voiceContext.UnfocusProject();
			AddMessage("System", $"Unfocused from '{name}'.", isUser: false);
			return true;
		}
		return false;
	}

	private async Task SendToFocusedProjectAsync(string text)
	{
		var focus = _voiceContext.Focus!;
		var prefixed = $"be as brief as possible. {text}";

		AddMessage("You", text, isUser: true);

		try
		{
			var projectService = App.Services.GetRequiredService<IProjectService>();
			await projectService.SendInputAsync(focus.ProfileName, focus.HostId, focus.ProjectId, prefixed);
			StatusText = $"Sent to {focus.ProjectName}";
		}
		catch (Exception ex)
		{
			AddMessage("Error", $"Failed to send: {ex.Message}", isUser: false, isError: true);
		}
	}

	[RelayCommand]
	private async Task ListenAsync()
	{
		_cts?.Cancel();
		_cts = new CancellationTokenSource();

		IsBusy = true;
		try
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			AssistantLog.Write("AUDIO", $"Capture started (engine: {_recognizer.EngineName})");
			StatusText = "Listening...";
			var transcript = await _recognizer.RecognizeSpeechAsync(_cts.Token);
			var sttMs = sw.ElapsedMilliseconds;
			AssistantLog.Write("AUDIO", $"Capture finished ({sttMs}ms)");

			if (string.IsNullOrWhiteSpace(transcript))
			{
				AssistantLog.Write("AUDIO", $"No speech detected ({sttMs}ms)");
				StatusText = $"No speech detected. [STT: {sttMs}ms]";
				return;
			}

			AssistantLog.Write("TIMING", $"STT: {sttMs}ms");

			// Keyword intercept (no AI needed)
			if (_voiceContext.Focus is not null && TryHandleUnfocusKeyword(transcript))
				return;

			// Focus mode: bypass AI, send directly to project
			if (_voiceContext.Focus is not null)
			{
				await SendToFocusedProjectAsync(transcript);
				return;
			}

			// Normal mode: through local AI
			if (!_assistant.IsModelLoaded)
			{
				AddMessage("System", "Please load the AI model first.", isUser: false);
				return;
			}

			AddMessage("You", transcript, isUser: true);
			await _assistant.ProcessTextAsync(transcript, _cts.Token);
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			AddMessage("Error", ex.Message, isUser: false, isError: true);
		}
		finally
		{
			IsBusy = false;
		}
	}

	[RelayCommand]
	private async Task SendAsync()
	{
		var text = InputText.Trim();
		if (string.IsNullOrEmpty(text)) return;

		InputText = string.Empty;

		// Keyword intercept (no AI needed)
		if (_voiceContext.Focus is not null && TryHandleUnfocusKeyword(text))
			return;

		// Focus mode: bypass AI, send directly to project
		if (_voiceContext.Focus is not null)
		{
			await SendToFocusedProjectAsync(text);
			return;
		}

		// Normal mode: through local AI
		if (!_assistant.IsModelLoaded)
		{
			AddMessage("System", "Please load the AI model first.", isUser: false);
			return;
		}

		AddMessage("You", text, isUser: true);

		_cts?.Cancel();
		_cts = new CancellationTokenSource();

		IsBusy = true;
		try
		{
			await _assistant.ProcessTextAsync(text, _cts.Token);
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			AddMessage("Error", ex.Message, isUser: false, isError: true);
		}
		finally
		{
			IsBusy = false;
		}
	}

	private void AddMessage(string sender, string text, bool isUser, bool isError = false)
	{
		Messages.Add(new VoiceMessage(sender, text, isUser, isError));
	}
}
