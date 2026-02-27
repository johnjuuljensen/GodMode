using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GodMode.Avalonia.Controls;

public partial class FileWriteView : UserControl
{
	public static readonly StyledProperty<string> FilePathProperty =
		AvaloniaProperty.Register<FileWriteView, string>(nameof(FilePath), "");

	public static readonly StyledProperty<string> FileContentProperty =
		AvaloniaProperty.Register<FileWriteView, string>(nameof(FileContent), "");

	public static readonly StyledProperty<bool> IsExpandedProperty =
		AvaloniaProperty.Register<FileWriteView, bool>(nameof(IsExpanded), false);

	public string FilePath
	{
		get => GetValue(FilePathProperty);
		set => SetValue(FilePathProperty, value);
	}

	public string FileContent
	{
		get => GetValue(FileContentProperty);
		set => SetValue(FileContentProperty, value);
	}

	public bool IsExpanded
	{
		get => GetValue(IsExpandedProperty);
		set => SetValue(IsExpandedProperty, value);
	}

	public string Preview
	{
		get
		{
			var lines = (FileContent ?? "").Split('\n');
			var previewLines = lines.Take(20);
			var result = string.Join("\n", previewLines);
			if (lines.Length > 20) result += $"\n... ({lines.Length - 20} more lines)";
			return result;
		}
	}

	public FileWriteView()
	{
		InitializeComponent();
	}

	private void ToggleExpanded(object? sender, RoutedEventArgs e)
	{
		IsExpanded = !IsExpanded;
	}
}
