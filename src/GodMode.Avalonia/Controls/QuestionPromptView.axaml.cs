using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GodMode.Shared.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GodMode.Avalonia.Controls;

public class QuestionOption : INotifyPropertyChanged
{
	public event PropertyChangedEventHandler? PropertyChanged;
	private void OnPropertyChanged([CallerMemberName] string? name = null)
		=> PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

	public string Label { get; set; } = "";
	public string? Description { get; set; }
	public bool HasDescription => !string.IsNullOrEmpty(Description);
	public int Index { get; set; }

	private bool _isActive;
	public bool IsActive
	{
		get => _isActive;
		set
		{
			if (_isActive == value) return;
			_isActive = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(BarBrush));
			OnPropertyChanged(nameof(RowBackground));
			OnPropertyChanged(nameof(LabelForeground));
		}
	}

	// These get set by the parent control when theme resources are available
	public IBrush AccentBrush { get; set; } = Brushes.DodgerBlue;
	public IBrush AccentMutedBrush { get; set; } = Brushes.Transparent;
	public IBrush TextPrimaryBrush { get; set; } = Brushes.White;
	public IBrush TransparentBrush { get; set; } = Brushes.Transparent;

	public IBrush BarBrush => IsActive ? AccentBrush : TransparentBrush;
	public IBrush RowBackground => IsActive ? AccentMutedBrush : TransparentBrush;
	public IBrush LabelForeground => IsActive ? AccentBrush : TextPrimaryBrush;
}

public partial class QuestionPromptView : UserControl
{
	public static readonly StyledProperty<string> QuestionTextProperty =
		AvaloniaProperty.Register<QuestionPromptView, string>(nameof(QuestionText), "");

	public static readonly StyledProperty<string?> AgentNameProperty =
		AvaloniaProperty.Register<QuestionPromptView, string?>(nameof(AgentName));

	public static readonly StyledProperty<ObservableCollection<QuestionOption>> OptionsProperty =
		AvaloniaProperty.Register<QuestionPromptView, ObservableCollection<QuestionOption>>(
			nameof(Options), new ObservableCollection<QuestionOption>());

	public static readonly StyledProperty<bool> HasOptionsProperty =
		AvaloniaProperty.Register<QuestionPromptView, bool>(nameof(HasOptions));

	private int _activeIndex; // 0..Options.Count-1 = regular options, Options.Count = "Other"
	private bool _isOtherEditing;

	// Cached theme brushes for "Other" row styling
	private IBrush _accentBrush = Brushes.DodgerBlue;
	private IBrush _accentMutedBrush = Brushes.Transparent;

	public string QuestionText
	{
		get => GetValue(QuestionTextProperty);
		set => SetValue(QuestionTextProperty, value);
	}

	public string? AgentName
	{
		get => GetValue(AgentNameProperty);
		set => SetValue(AgentNameProperty, value);
	}

	public ObservableCollection<QuestionOption> Options
	{
		get => GetValue(OptionsProperty);
		set => SetValue(OptionsProperty, value);
	}

	public bool HasOptions
	{
		get => GetValue(HasOptionsProperty);
		set => SetValue(HasOptionsProperty, value);
	}

	public event EventHandler<string>? OptionSelected;
	public event EventHandler? Dismissed;

	private bool IsOtherActive => _activeIndex == Options.Count;
	private int TotalCount => Options.Count + 1; // regular options + "Other"

	public QuestionPromptView()
	{
		InitializeComponent();

		OtherTextBox.AddHandler(KeyDownEvent, OnOtherTextBoxKeyDown, RoutingStrategies.Tunnel);
	}

	public void SetOptions(IEnumerable<QuestionOptionData> optionData)
	{
		_accentBrush = this.FindResource("AccentBrush") as IBrush ?? Brushes.DodgerBlue;
		_accentMutedBrush = this.FindResource("AccentMutedBrush") as IBrush ?? Brushes.Transparent;
		var textPrimaryBrush = this.FindResource("TextPrimaryBrush") as IBrush ?? Brushes.White;

		var opts = new ObservableCollection<QuestionOption>();
		int i = 0;
		foreach (var data in optionData)
		{
			opts.Add(new QuestionOption
			{
				Label = data.Label,
				Description = data.Description,
				Index = i,
				AccentBrush = _accentBrush,
				AccentMutedBrush = _accentMutedBrush,
				TextPrimaryBrush = textPrimaryBrush,
			});
			i++;
		}
		Options = opts;
		HasOptions = opts.Count > 0;
		_activeIndex = 0;
		_isOtherEditing = false;
		OtherTextBox.Text = "";
		OtherTextBox.IsVisible = false;
		OtherLabel.IsVisible = true;
		if (HasOptions) UpdateActive();
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);

		if (e.Key == Key.Escape)
		{
			if (_isOtherEditing)
			{
				// Exit "Other" editing mode, go back to option navigation
				ExitOtherEditing();
				this.Focus();
				e.Handled = true;
				return;
			}
			Dismissed?.Invoke(this, EventArgs.Empty);
			e.Handled = true;
			return;
		}

		if (Options.Count == 0) return;

		// Alt+Up/Down: scroll parent without changing focus
		if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
		{
			if (e.Key is Key.Up or Key.Down)
			{
				var scrollViewer = this.FindAncestorOfType<ScrollViewer>();
				if (scrollViewer != null)
				{
					var delta = e.Key == Key.Up ? -40 : 40;
					scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Max(0, scrollViewer.Offset.Y + delta));
				}
				e.Handled = true;
				return;
			}
		}

		// Don't intercept navigation keys while editing "Other" text
		if (_isOtherEditing) return;

		switch (e.Key)
		{
			case Key.Up:
				_activeIndex = (_activeIndex - 1 + TotalCount) % TotalCount;
				UpdateActive();
				e.Handled = true;
				break;
			case Key.Down:
			case Key.Tab when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
				_activeIndex = (_activeIndex + 1) % TotalCount;
				UpdateActive();
				e.Handled = true;
				break;
			case Key.Tab when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
				_activeIndex = (_activeIndex - 1 + TotalCount) % TotalCount;
				UpdateActive();
				e.Handled = true;
				break;
			case Key.Enter:
			case Key.Space:
				if (IsOtherActive)
					EnterOtherEditing();
				else
					ConfirmOption(_activeIndex);
				e.Handled = true;
				break;
			default:
				// Number key selection (1-9)
				if (e.Key >= Key.D1 && e.Key <= Key.D9)
				{
					var idx = e.Key - Key.D1;
					if (idx < Options.Count)
					{
						_activeIndex = idx;
						UpdateActive();
						ConfirmOption(idx);
						e.Handled = true;
					}
				}
				// Key 0 → option 10
				else if (e.Key == Key.D0 && Options.Count >= 10)
				{
					_activeIndex = 9;
					UpdateActive();
					ConfirmOption(9);
					e.Handled = true;
				}
				break;
		}
	}

	private void OnOtherTextBoxKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
		{
			var text = OtherTextBox.Text?.Trim();
			if (!string.IsNullOrEmpty(text))
			{
				OptionSelected?.Invoke(this, text);
			}
			e.Handled = true;
		}
		else if (e.Key == Key.Escape)
		{
			ExitOtherEditing();
			this.Focus();
			e.Handled = true;
		}
	}

	private void EnterOtherEditing()
	{
		_isOtherEditing = true;
		OtherLabel.IsVisible = false;
		OtherTextBox.IsVisible = true;
		OtherTextBox.Text = "";
		OtherTextBox.Focus();
	}

	private void ExitOtherEditing()
	{
		_isOtherEditing = false;
		OtherTextBox.IsVisible = false;
		OtherLabel.IsVisible = true;
	}

	/// <summary>
	/// Confirms an option with a brief flash animation before firing the event.
	/// </summary>
	private void ConfirmOption(int index)
	{
		if (index < 0 || index >= Options.Count) return;

		var option = Options[index];
		option.IsActive = true;

		// Brief delay then fire selection
		var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
		timer.Tick += (_, _) =>
		{
			timer.Stop();
			OptionSelected?.Invoke(this, option.Label);
		};
		timer.Start();
	}

	private void OnDismissClicked(object? sender, RoutedEventArgs e)
		=> Dismissed?.Invoke(this, EventArgs.Empty);

	private void OnOptionClicked(object? sender, RoutedEventArgs e)
	{
		if (sender is Button btn && btn.Tag is QuestionOption option)
			ConfirmOption(option.Index);
	}

	private void OnOptionPointerEntered(object? sender, PointerEventArgs e)
	{
		if (sender is Button btn && btn.Tag is QuestionOption option)
		{
			_activeIndex = option.Index;
			UpdateActive();
		}
	}

	private void OnOtherClicked(object? sender, RoutedEventArgs e)
	{
		_activeIndex = Options.Count;
		UpdateActive();
		EnterOtherEditing();
	}

	private void OnOtherPointerEntered(object? sender, PointerEventArgs e)
	{
		_activeIndex = Options.Count;
		UpdateActive();
	}

	private void UpdateActive()
	{
		// Update regular options
		for (int i = 0; i < Options.Count; i++)
			Options[i].IsActive = i == _activeIndex;

		// Update "Other" row styling
		var otherActive = IsOtherActive;
		OtherRowBorder.Background = otherActive ? _accentMutedBrush : Brushes.Transparent;
		OtherBarIndicator.Background = otherActive ? _accentBrush : Brushes.Transparent;
		OtherEnterHint.IsVisible = otherActive && !_isOtherEditing;

		if (!otherActive && _isOtherEditing)
			ExitOtherEditing();
	}
}
