using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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

	private int _activeIndex;

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
	public event EventHandler? EscapePressed;

	public QuestionPromptView()
	{
		InitializeComponent();
	}

	public void SetOptions(IEnumerable<QuestionOptionData> optionData)
	{
		var accentBrush = this.FindResource("AccentBrush") as IBrush ?? Brushes.DodgerBlue;
		var accentMutedBrush = this.FindResource("AccentMutedBrush") as IBrush ?? Brushes.Transparent;
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
				AccentBrush = accentBrush,
				AccentMutedBrush = accentMutedBrush,
				TextPrimaryBrush = textPrimaryBrush,
			});
			i++;
		}
		Options = opts;
		HasOptions = opts.Count > 0;
		_activeIndex = 0;
		if (HasOptions) UpdateActive();
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);

		if (e.Key == Key.Escape)
		{
			EscapePressed?.Invoke(this, EventArgs.Empty);
			e.Handled = true;
			return;
		}

		if (Options.Count == 0) return;

		switch (e.Key)
		{
			case Key.Up:
				_activeIndex = (_activeIndex - 1 + Options.Count) % Options.Count;
				UpdateActive();
				e.Handled = true;
				break;
			case Key.Down:
				_activeIndex = (_activeIndex + 1) % Options.Count;
				UpdateActive();
				e.Handled = true;
				break;
			case Key.Enter:
				if (_activeIndex >= 0 && _activeIndex < Options.Count)
				{
					_ = ConfirmAndSelectAsync(Options[_activeIndex].Label);
					e.Handled = true;
				}
				break;
			case Key.Tab:
				if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
				{
					_activeIndex = (_activeIndex - 1 + Options.Count) % Options.Count;
				}
				else
				{
					_activeIndex = (_activeIndex + 1) % Options.Count;
				}
				UpdateActive();
				e.Handled = true;
				break;
			default:
				// Number key selection (1-9, 0 for 10)
				if (e.Key >= Key.D1 && e.Key <= Key.D9)
				{
					var idx = e.Key - Key.D1;
					if (idx < Options.Count)
					{
						_activeIndex = idx;
						UpdateActive();
						_ = ConfirmAndSelectAsync(Options[idx].Label);
						e.Handled = true;
					}
				}
				else if (e.Key == Key.D0 && Options.Count >= 10)
				{
					_activeIndex = 9;
					UpdateActive();
					_ = ConfirmAndSelectAsync(Options[9].Label);
					e.Handled = true;
				}
				break;
		}
	}

	private async Task ConfirmAndSelectAsync(string label)
	{
		// Brief visual feedback before sending
		await Task.Delay(120);
		OptionSelected?.Invoke(this, label);
	}

	private void OnOptionClicked(object? sender, RoutedEventArgs e)
	{
		if (sender is Button btn && btn.Tag is QuestionOption option)
			_ = ConfirmAndSelectAsync(option.Label);
	}

	private void OnOptionPointerEntered(object? sender, PointerEventArgs e)
	{
		if (sender is Button btn && btn.Tag is QuestionOption option)
		{
			_activeIndex = option.Index;
			UpdateActive();
		}
	}

	private void UpdateActive()
	{
		for (int i = 0; i < Options.Count; i++)
			Options[i].IsActive = i == _activeIndex;
	}
}
