using System.Globalization;

namespace GodMode.Maui.Converters;

/// <summary>
/// Converts Claude message type string to accent colors for borders and labels.
/// These are vibrant colors that work well as accents in both light and dark themes.
/// </summary>
public class MessageTypeToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string type)
        {
            return type.ToLowerInvariant() switch
            {
                "user" => Color.FromArgb("#2196F3"),      // Blue
                "assistant" => Color.FromArgb("#4CAF50"), // Green
                "result" => Color.FromArgb("#009688"),    // Teal
                "error" => Color.FromArgb("#F44336"),     // Red
                "system" => Color.FromArgb("#9C27B0"),    // Purple
                _ => Color.FromArgb("#757575")            // Gray
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
/// Converts IsUserMessage bool to HorizontalOptions for chat bubble alignment.
/// User messages align to Start (left), others align to End (right).
/// </summary>
public class IsUserToAlignmentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isUser)
        {
            return isUser ? LayoutOptions.Start : LayoutOptions.End;
        }
        return LayoutOptions.Fill;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
