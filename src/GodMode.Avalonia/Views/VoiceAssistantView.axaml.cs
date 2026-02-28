using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System.Globalization;

namespace GodMode.Avalonia.Views;

public partial class VoiceAssistantView : UserControl
{
	public static readonly IValueConverter AlignmentConverter =
		new FuncValueConverter<bool, HorizontalAlignment>(isUser =>
			isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left);

	public static readonly IMultiValueConverter MessageBgConverter =
		new MessageBackgroundConverter();

	public VoiceAssistantView()
	{
		InitializeComponent();
	}

	private void InputBox_KeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && DataContext is VoiceAssistantViewModel vm)
		{
			e.Handled = true;
			vm.SendCommand.Execute(null);
		}
	}

	private sealed class MessageBackgroundConverter : IMultiValueConverter
	{
		public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
		{
			if (values.Count < 2) return Brushes.Transparent;

			var isUser = values[0] is true;
			var isError = values[1] is true;

			if (isError)
				return new SolidColorBrush(Color.FromRgb(0x44, 0x1C, 0x22)); // dark red tint
			if (isUser)
				return new SolidColorBrush(Color.FromRgb(0x1C, 0x2D, 0x44)); // dark blue tint (accent)

			return new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)); // CardBrush equivalent
		}
	}
}
