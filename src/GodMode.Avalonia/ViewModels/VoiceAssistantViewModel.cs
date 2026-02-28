using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.Avalonia.Voice;
using GodMode.Shared.Models;
using GodMode.Voice.Services;
using GodMode.Voice.Speech;
using GodMode.Voice.Tools;
using GodMode.Voice.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace GodMode.Avalonia.ViewModels;

public partial class VoiceAssistantViewModel : ViewModelBase
{
	private readonly AssistantService _assistant;
	private readonly VoiceContext _voiceContext;
	private readonly InferenceConfig _config;
	private readonly ISpeechRecognizer _recognizer;
	private CancellationTokenSource? _cts;

	private static readonly string[] UnfocusKeywords =
		["unfocus", "exit focus", "stop watching", "leave project"];

	[ObservableProperty]
	private string _statusText = "Initializing...";

	[ObservableProperty]
	private string _inputText = string.Empty;

	[ObservableProperty]
	private string _modelPath = string.Empty;

	[ObservableProperty]
	private bool _isBusy;

	[ObservableProperty]
	private bool _isModelLoaded;

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
		ISpeechRecognizer recognizer,
		VoiceContext voiceContext)
		: base(navigationService)
	{
		_assistant = assistant;
		_recognizer = recognizer;
		_voiceContext = voiceContext;
		_config = InferenceConfig.Load();

		SttEngineName = $"STT: {_recognizer.EngineName}";
		ConfigPath = $"Config: {InferenceConfig.ConfigPath}";

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

		// Subscribe to focused project output
		_voiceContext.ProjectOutputReceived += OnProjectOutputReceived;

		// Check if model was already loaded at startup
		if (!string.IsNullOrEmpty(_config.Phi4ModelPath))
		{
			ModelPath = _config.Phi4ModelPath;

			if (_assistant.IsModelLoaded)
			{
				IsModelLoaded = true;
				StatusText = "Model loaded.";
			}
			else if (Directory.Exists(_config.Phi4ModelPath) &&
				File.Exists(Path.Combine(_config.Phi4ModelPath, "genai_config.json")))
			{
				StatusText = "Model loading in background...";
				// Poll for model load completion from the startup fire-and-forget
				_ = WaitForModelLoadAsync();
			}
			else
			{
				StatusText = "Model path in config but files missing. Update path and click Load Model.";
			}
		}
		else
		{
			ShowFirstRunMessage();
		}
	}

	private async Task WaitForModelLoadAsync()
	{
		// Wait for the startup background load to complete
		while (!_assistant.IsModelLoaded)
			await Task.Delay(500);

		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			IsModelLoaded = true;
			StatusText = "Model loaded.";
			AddMessage("System", "Model loaded automatically from config.", isUser: false);
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
			languages = WindowsSpeechRecognizer.GetInstalledLanguages().ToList();
		}
		catch
		{
			languages = ["en-US"];
		}

		SpeechLanguages = new ObservableCollection<string>(languages);

		var configured = _config.SpeechLanguage;
		var match = languages.FirstOrDefault(l =>
			l.Equals(configured, StringComparison.OrdinalIgnoreCase));

		SelectedLanguage = match ?? languages.FirstOrDefault();
	}

	partial void OnSelectedLanguageChanged(string? value)
	{
		if (value is not null && value != _config.SpeechLanguage)
		{
			_config.SpeechLanguage = value;
			_config.Save();
		}
	}

	private void ShowFirstRunMessage()
	{
		StatusText = "First run — configure model path below.";
		AddMessage("System",
			$"Welcome to GodMode Voice Assistant!\n\n" +
			$"No model configured yet. To get started:\n" +
			$"1. Download Phi-4-mini ONNX from HuggingFace:\n" +
			$"   huggingface-cli download microsoft/Phi-4-mini-instruct-onnx --local-dir <path>\n" +
			$"2. Set the path below and click Load Model\n" +
			$"3. The path will be saved to:\n" +
			$"   {InferenceConfig.ConfigPath}",
			isUser: false);
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
	private async Task LoadModelAsync()
	{
		var path = ModelPath.Trim();
		if (string.IsNullOrEmpty(path))
		{
			AddMessage("System", "Please enter a model directory path first.", isUser: false);
			return;
		}

		var fullPath = Path.GetFullPath(path);
		if (!Directory.Exists(fullPath))
		{
			AddMessage("Error", $"Directory not found: {fullPath}", isUser: false, isError: true);
			return;
		}

		if (!File.Exists(Path.Combine(fullPath, "genai_config.json")))
		{
			AddMessage("Error",
				$"Not a valid model directory (genai_config.json missing):\n{fullPath}\n\n" +
				$"Expected files: genai_config.json, tokenizer.json, model.onnx",
				isUser: false, isError: true);
			return;
		}

		IsBusy = true;
		try
		{
			await _assistant.InitializeModelAsync(fullPath);
			IsModelLoaded = true;

			_config.Phi4ModelPath = fullPath;
			_config.Save();

			AddMessage("System", $"Model loaded successfully.\nPath saved to {InferenceConfig.ConfigPath}", isUser: false);
		}
		catch (Exception ex)
		{
			AddMessage("Error", $"Failed to load model: {ex.Message}", isUser: false, isError: true);
		}
		finally
		{
			IsBusy = false;
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
			StatusText = "Listening...";
			var transcript = await _recognizer.RecognizeSpeechAsync(_cts.Token);

			if (string.IsNullOrWhiteSpace(transcript))
			{
				StatusText = "No speech detected.";
				return;
			}

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
