using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
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
	private bool _scrollPending;
	private DispatcherTimer? _scrollTimer;
	private const double ScrollThreshold = 50; // px from bottom to count as "at bottom"

	public ProjectView()
	{
		InitializeComponent();

		// Intercept Enter key on input (tunnel to catch before TextBox processes it)
		InputEditor.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

		// Wire question prompt events
		QuestionPrompt.OptionSelected += OnQuestionOptionSelected;
		QuestionPrompt.Dismissed += OnQuestionDismissed;

		// Debounced scroll-to-end timer (coalesces rapid message adds)
		_scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
		_scrollTimer.Tick += (_, _) =>
		{
			_scrollTimer.Stop();
			if (_scrollPending)
			{
				_scrollPending = false;
				OutputScrollViewer.ScrollToEnd();
			}
		};

		DataContextChanged += (_, _) =>
		{
			if (DataContext is ProjectViewModel vm)
			{
				vm.DisplayMessages.CollectionChanged += OnDisplayMessagesChanged;
				vm.PropertyChanged += OnViewModelPropertyChanged;

				// Scroll to bottom when opening a project with existing messages
				if (vm.DisplayMessages.Count > 0)
				{
					_isAtBottom = true;
					Dispatcher.UIThread.Post(() =>
						OutputScrollViewer.ScrollToEnd(),
						DispatcherPriority.Background);
				}
			}
		};

		// Track scroll position to determine if user has scrolled up
		OutputScrollViewer.ScrollChanged += (_, _) =>
		{
			var sv = OutputScrollViewer;
			var extent = sv.Extent.Height;
			var viewport = sv.Viewport.Height;
			var offset = sv.Offset.Y;

			// User is "at bottom" if within threshold of the end
			_isAtBottom = (extent - viewport - offset) < ScrollThreshold;
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

	private void OnQuestionDismissed(object? sender, EventArgs e)
	{
		if (DataContext is ProjectViewModel vm)
		{
			vm.DismissQuestion();
			InputEditor.Focus();
		}
	}

	private void OnDisplayMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
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
				if (_isInitialLoad && vm.DisplayMessages.Count > 5)
					_isInitialLoad = false;

				// Debounce: restart timer on each add to coalesce rapid messages
				_scrollPending = true;
				_scrollTimer!.Stop();
				_scrollTimer.Start();
			}
		}
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnDetachedFromVisualTree(e);

		_scrollTimer?.Stop();

		if (DataContext is ProjectViewModel vm)
		{
			vm.DisplayMessages.CollectionChanged -= OnDisplayMessagesChanged;
			vm.PropertyChanged -= OnViewModelPropertyChanged;
			vm.Dispose();
		}
	}
}
