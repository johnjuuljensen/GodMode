using Avalonia;
using Avalonia.Controls;

namespace GodMode.Avalonia.Controls;

public partial class BashCommandView : UserControl
{
	public static readonly StyledProperty<string> CommandProperty =
		AvaloniaProperty.Register<BashCommandView, string>(nameof(Command), "");

	public static readonly StyledProperty<string?> DescriptionProperty =
		AvaloniaProperty.Register<BashCommandView, string?>(nameof(Description));

	public string Command
	{
		get => GetValue(CommandProperty);
		set => SetValue(CommandProperty, value);
	}

	public string? Description
	{
		get => GetValue(DescriptionProperty);
		set => SetValue(DescriptionProperty, value);
	}

	public BashCommandView()
	{
		InitializeComponent();
	}
}
