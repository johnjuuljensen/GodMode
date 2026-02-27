using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace GodMode.Avalonia.Converters;

public class BoolToColorConverter : IValueConverter
{
	private static readonly IBrush GreenBrush = new SolidColorBrush(Color.Parse("#30D158"));
	private static readonly IBrush RedBrush = new SolidColorBrush(Color.Parse("#FF453A"));
	private static readonly IBrush GrayBrush = new SolidColorBrush(Color.Parse("#59FFFFFF"));

	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is bool isConnected)
			return isConnected ? GreenBrush : RedBrush;
		return GrayBrush;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotImplementedException();
}

public class BoolToActiveBrushConverter : IValueConverter
{
	private static readonly IBrush Active = new SolidColorBrush(Color.Parse("#1FFFFFFF"));
	private static readonly IBrush Inactive = Brushes.Transparent;

	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> value is true ? Active : Inactive;

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotImplementedException();
}
