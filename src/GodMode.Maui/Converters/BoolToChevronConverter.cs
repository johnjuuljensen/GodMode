using System.Globalization;

namespace GodMode.Maui.Converters;

/// <summary>
/// Converts a boolean to a chevron character for expand/collapse indicators.
/// </summary>
public class BoolToChevronConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isExpanded)
        {
            return isExpanded ? "\u25BC" : "\u25B6"; // ▼ or ▶
        }
        return "\u25B6"; // Default to collapsed
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
