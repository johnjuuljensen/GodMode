using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace GodMode.Avalonia.Converters;

public class BoolToColorConverter : IValueConverter
{
	private static readonly IBrush GreenBrush = new SolidColorBrush(Color.Parse("#4CAF50"));
	private static readonly IBrush RedBrush = new SolidColorBrush(Color.Parse("#F44336"));
	private static readonly IBrush GrayBrush = new SolidColorBrush(Color.Parse("#9E9E9E"));

	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is bool isConnected)
			return isConnected ? GreenBrush : RedBrush;
		return GrayBrush;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotImplementedException();
}
