using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace GodMode.Avalonia.Controls;

public partial class DiffView : UserControl
{
	public static readonly StyledProperty<string> FilePathProperty =
		AvaloniaProperty.Register<DiffView, string>(nameof(FilePath), "");

	public static readonly StyledProperty<string> OldStringProperty =
		AvaloniaProperty.Register<DiffView, string>(nameof(OldString), "");

	public static readonly StyledProperty<string> NewStringProperty =
		AvaloniaProperty.Register<DiffView, string>(nameof(NewString), "");

	public static readonly StyledProperty<bool> IsExpandedProperty =
		AvaloniaProperty.Register<DiffView, bool>(nameof(IsExpanded), false);

	public string FilePath
	{
		get => GetValue(FilePathProperty);
		set => SetValue(FilePathProperty, value);
	}

	public string OldString
	{
		get => GetValue(OldStringProperty);
		set => SetValue(OldStringProperty, value);
	}

	public string NewString
	{
		get => GetValue(NewStringProperty);
		set => SetValue(NewStringProperty, value);
	}

	public bool IsExpanded
	{
		get => GetValue(IsExpandedProperty);
		set => SetValue(IsExpandedProperty, value);
	}

	public string DiffSummary
	{
		get
		{
			var oldLines = (OldString ?? "").Split('\n').Length;
			var newLines = (NewString ?? "").Split('\n').Length;
			return $"-{oldLines} +{newLines}";
		}
	}

	public DiffView()
	{
		InitializeComponent();
	}

	private void ToggleExpanded(object? sender, RoutedEventArgs e)
	{
		IsExpanded = !IsExpanded;
		if (IsExpanded)
			RenderDiff();
	}

	private void RenderDiff()
	{
		DiffLines.Children.Clear();
		var removedBg = new SolidColorBrush(Color.Parse("#30FF453A"));
		var addedBg = new SolidColorBrush(Color.Parse("#3030D158"));
		var removedFg = new SolidColorBrush(Color.Parse("#FFFF453A"));
		var addedFg = new SolidColorBrush(Color.Parse("#FF30D158"));
		var defaultFg = new SolidColorBrush(Color.Parse("#8CFFFFFF"));
		var font = new FontFamily("Menlo, Consolas, monospace");

		var oldLines = (OldString ?? "").Split('\n');
		var newLines = (NewString ?? "").Split('\n');

		foreach (var line in oldLines)
		{
			DiffLines.Children.Add(new TextBlock
			{
				Text = $"- {line}",
				FontSize = 11,
				FontFamily = font,
				Foreground = removedFg,
				Background = removedBg,
				Padding = new global::Avalonia.Thickness(10, 1)
			});
		}

		foreach (var line in newLines)
		{
			DiffLines.Children.Add(new TextBlock
			{
				Text = $"+ {line}",
				FontSize = 11,
				FontFamily = font,
				Foreground = addedFg,
				Background = addedBg,
				Padding = new global::Avalonia.Thickness(10, 1)
			});
		}
	}
}
