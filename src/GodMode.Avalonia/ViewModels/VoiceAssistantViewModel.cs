using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.Voice.Services;
using GodMode.Voice.Speech;
using GodMode.Voice.Windows;

namespace GodMode.Avalonia.ViewModels;

public partial class VoiceAssistantViewModel : ViewModelBase
{
	private readonly AssistantService _assistant;
	private readonly InferenceConfig _config;
	private readonly ISpeechRecognizer _recognizer;
	private CancellationTokenSource? _cts;

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
		ISpeechRecognizer recognizer)
		: base(navigationService)
	{
		_assistant = assistant;
		_recognizer = recognizer;
		_config = InferenceConfig.Load();

		SttEngineName = $"STT: {_recognizer.EngineName}";
		ConfigPath = $"Config: {InferenceConfig.ConfigPath}";

		PopulateLanguages();

		_assistant.StatusChanged += (_, status) =>
			Dispatcher.UIThread.Post(() => StatusText = status);

		_assistant.TranscriptUpdated += (_, text) =>
			Dispatcher.UIThread.Post(() => AddMessage("You", text, isUser: true));

		_assistant.ResponseReceived += (_, text) =>
			Dispatcher.UIThread.Post(() => AddMessage("Assistant", text, isUser: false));

		_assistant.ErrorOccurred += (_, error) =>
			Dispatcher.UIThread.Post(() => AddMessage("Error", error, isUser: false, isError: true));

		if (!string.IsNullOrEmpty(_config.Phi4ModelPath))
		{
			ModelPath = _config.Phi4ModelPath;

			if (Directory.Exists(_config.Phi4ModelPath) &&
				File.Exists(Path.Combine(_config.Phi4ModelPath, "genai_config.json")))
			{
				_ = AutoLoadModelAsync(_config.Phi4ModelPath);
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

	private async Task AutoLoadModelAsync(string path)
	{
		IsBusy = true;
		try
		{
			await _assistant.InitializeModelAsync(path);
			IsModelLoaded = true;
			AddMessage("System", "Model loaded automatically from config.", isUser: false);
		}
		catch (Exception ex)
		{
			AddMessage("Error", $"Failed to auto-load model: {ex.Message}", isUser: false, isError: true);
		}
		finally
		{
			IsBusy = false;
		}
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
			$"Welcome to Godmode Voice Assistant!\n\n" +
			$"No model configured yet. To get started:\n" +
			$"1. Download Phi-4-mini ONNX from HuggingFace:\n" +
			$"   huggingface-cli download microsoft/Phi-4-mini-instruct-onnx --local-dir <path>\n" +
			$"2. Set the path below and click Load Model\n" +
			$"3. The path will be saved to:\n" +
			$"   {InferenceConfig.ConfigPath}",
			isUser: false);
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
		if (!_assistant.IsModelLoaded)
		{
			AddMessage("System", "Please load the AI model first.", isUser: false);
			return;
		}

		_cts?.Cancel();
		_cts = new CancellationTokenSource();

		IsBusy = true;
		try
		{
			await _assistant.ListenAndProcessAsync(_cts.Token);
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

		if (!_assistant.IsModelLoaded)
		{
			AddMessage("System", "Please load the AI model first.", isUser: false);
			return;
		}

		InputText = string.Empty;
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

	[RelayCommand]
	private void GoBack()
	{
		Navigation.GoBack();
	}

	private void AddMessage(string sender, string text, bool isUser, bool isError = false)
	{
		Messages.Add(new VoiceMessage(sender, text, isUser, isError));
	}
}
