using GodMode.Shared.Enums;
using System.Globalization;

namespace GodMode.Maui.Converters;

/// <summary>
/// Converts OutputEventType to accent colors for borders and labels.
/// These are vibrant colors that work well as accents in both light and dark themes.
/// </summary>
public class OutputTypeToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is OutputEventType type)
        {
            return type switch
            {
                OutputEventType.User => Color.FromArgb("#2196F3"),      // Blue
                OutputEventType.Assistant => Color.FromArgb("#4CAF50"), // Green
                OutputEventType.Result => Color.FromArgb("#009688"),    // Teal
                OutputEventType.Error => Color.FromArgb("#F44336"),     // Red
                OutputEventType.System => Color.FromArgb("#9C27B0"),    // Purple
                _ => Color.FromArgb("#757575")                          // Gray
            };
        }
        return Color.FromArgb("#757575");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts OutputEventType to HorizontalOptions for chat bubble alignment.
/// User messages align to Start (left), others align to End (right).
/// </summary>
public class OutputTypeToAlignmentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is OutputEventType type)
        {
            return type switch
            {
                OutputEventType.User => LayoutOptions.Start,  // User messages on left
                _ => LayoutOptions.End                        // System/assistant messages on right
            };
        }
        return LayoutOptions.Fill;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
