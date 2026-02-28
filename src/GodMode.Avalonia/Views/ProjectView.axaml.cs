using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GodMode.Avalonia.ViewModels;

namespace GodMode.Avalonia.Views;

public partial class ProjectView : UserControl
{
	private bool _isInitialLoad = true;
	private bool _isAtBottom = true;
	private const double ScrollThreshold = 50; // px from bottom to count as "at bottom"

	public ProjectView()
	{
		InitializeComponent();

		// Intercept Enter key on input (tunnel to catch before TextBox processes it)
		InputEditor.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

		// Wire question prompt events
		QuestionPrompt.OptionSelected += OnQuestionOptionSelected;
		QuestionPrompt.EscapePressed += OnQuestionEscapePressed;

		// Wire scroll-to-question and floating answer buttons
		ScrollToQuestionButton.Click += OnScrollToQuestion;
		FloatingAnswerButton.Click += OnScrollToQuestion;

		DataContextChanged += (_, _) =>
		{
			if (DataContext is ProjectViewModel vm)
			{
				vm.OutputMessages.CollectionChanged += OnOutputMessagesChanged;
				vm.PropertyChanged += OnViewModelPropertyChanged;

				// Scroll to bottom when opening a project with existing messages
				if (vm.OutputMessages.Count > 0)
				{
					_isAtBottom = true;
					Dispatcher.UIThread.Post(() =>
						OutputScrollViewer.ScrollToEnd(),
						DispatcherPriority.Background);
				}

				// Re-render option picker if question is still active (QD-04)
				if (vm.IsQuestionActive)
				{
					Dispatcher.UIThread.Post(() =>
					{
						QuestionPrompt.QuestionText = vm.CurrentQuestionText ?? "";
						QuestionPrompt.AgentName = vm.CurrentQuestionHeader ?? vm.Status?.Name;
						QuestionPrompt.SetOptions(vm.CurrentQuestionOptions);
						QuestionPrompt.Focus();
					}, DispatcherPriority.Background);
				}
			}
		};

		// Track scroll position to determine if user has scrolled up
		OutputScrollViewer.ScrollChanged += (_, e) =>
		{
			var sv = OutputScrollViewer;
			var extent = sv.Extent.Height;
			var viewport = sv.Viewport.Height;
			var offset = sv.Offset.Y;

			_isAtBottom = (extent - viewport - offset) < ScrollThreshold;
			UpdateFloatingButton();
		};
	}

	private void OnInputKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
		{
			if (DataContext is ProjectViewModel vm && vm.SendInputCommand.CanExecute(null))
			{
				_ = vm.SendInputCommand.ExecuteAsync(null);
				e.Handled = true;
			}
		}
	}

	private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(ProjectViewModel.IsQuestionActive) && sender is ProjectViewModel vm)
		{
			Dispatcher.UIThread.Post(() =>
			{
				if (vm.IsQuestionActive)
				{
					QuestionPrompt.QuestionText = vm.CurrentQuestionText ?? "";
					QuestionPrompt.AgentName = vm.CurrentQuestionHeader ?? vm.Status?.Name;
					QuestionPrompt.SetOptions(vm.CurrentQuestionOptions);
					QuestionPrompt.Focus();
				}
				UpdateFloatingButton();
			});
		}
	}

	private void OnQuestionOptionSelected(object? sender, string selectedOption)
	{
		if (DataContext is ProjectViewModel vm)
		{
			vm.InputText = selectedOption;
			_ = vm.SendInputCommand.ExecuteAsync(null);
		}
	}

	private void OnQuestionEscapePressed(object? sender, EventArgs e)
	{
		if (DataContext is ProjectViewModel vm)
		{
			vm.IsInputLocked = false;
			InputEditor.Focus();
		}
	}

	private void OnScrollToQuestion(object? sender, RoutedEventArgs e)
	{
		OutputScrollViewer.ScrollToEnd();
		QuestionPrompt.Focus();
	}

	private void UpdateFloatingButton()
	{
		if (DataContext is ProjectViewModel vm && vm.IsQuestionActive && !_isAtBottom)
			FloatingAnswerButton.IsVisible = true;
		else
			FloatingAnswerButton.IsVisible = false;
	}

	private void OnOutputMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		if (DataContext is not ProjectViewModel vm) return;

		if (e.Action == NotifyCollectionChangedAction.Reset)
		{
			_isInitialLoad = true;
			_isAtBottom = true;
			return;
		}

		if (e.Action == NotifyCollectionChangedAction.Add)
		{
			if (_isAtBottom || _isInitialLoad)
			{
				if (_isInitialLoad && vm.OutputMessages.Count > 5)
					_isInitialLoad = false;

				Dispatcher.UIThread.Post(() =>
				{
					OutputScrollViewer.ScrollToEnd();
				}, DispatcherPriority.Background);
			}
		}
	}

	protected override void OnDetachedFromVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
	{
		base.OnDetachedFromVisualTree(e);

		if (DataContext is ProjectViewModel vm)
		{
			vm.OutputMessages.CollectionChanged -= OnOutputMessagesChanged;
			vm.PropertyChanged -= OnViewModelPropertyChanged;
			// Don't dispose VM — it's cached in MainWindowViewModel._projectViewModels
			// and needs to survive navigation for state persistence (SP-01)
		}
	}
}
