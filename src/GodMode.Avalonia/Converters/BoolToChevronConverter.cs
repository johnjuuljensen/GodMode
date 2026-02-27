using System.Globalization;
using Avalonia.Data.Converters;

namespace GodMode.Avalonia.Converters;

public class BoolToChevronConverter : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is bool isExpanded)
			return isExpanded ? "\u25BC" : "\u25B6"; // down or right triangle
		return "\u25B6";
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotImplementedException();
}
