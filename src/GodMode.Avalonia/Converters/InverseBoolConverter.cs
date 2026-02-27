using System.Globalization;
using Avalonia.Data.Converters;

namespace GodMode.Avalonia.Converters;

public class InverseBoolConverter : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> value is bool b ? !b : false;

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> value is bool b ? !b : false;
}
