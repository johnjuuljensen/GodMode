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

/// <summary>
/// Converts IsSimpleMode bool to button text.
/// When in simple mode, button shows "Advanced" to switch to advanced.
/// When in advanced mode, button shows "Simple" to switch to simple.
/// </summary>
public class BoolToViewModeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isSimple)
        {
            return isSimple ? "Advanced" : "Simple";
        }
        return "Simple";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// MultiValueConverter for simple mode visibility.
/// Takes [IsSimpleMode, ShowInSimpleMode] and returns visibility.
/// Parameter "simple" returns true when both are true (show simple content).
/// Parameter "advanced" returns true when either is false (show advanced/properties).
/// </summary>
public class SimpleModeMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return false;

        var isSimpleMode = values[0] is bool b1 && b1;
        var showInSimpleMode = values[1] is bool b2 && b2;
        var mode = parameter as string ?? "simple";

        return mode == "simple"
            ? isSimpleMode && showInSimpleMode      // Show simple content
            : !isSimpleMode || !showInSimpleMode;   // Show advanced/properties
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converter for toggle button background colors.
/// Parameter "simple" returns active color when IsSimpleMode=true.
/// Parameter "advanced" returns active color when IsSimpleMode=false.
/// </summary>
public class BoolToToggleColorConverter : IValueConverter
{
    private static readonly Color ActiveColor = Color.FromArgb("#2196F3");
    private static readonly Color InactiveColor = Colors.Transparent;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isSimple = value is bool b && b;
        var mode = parameter as string ?? "simple";

        return mode == "simple"
            ? (isSimple ? ActiveColor : InactiveColor)
            : (isSimple ? InactiveColor : ActiveColor);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converter for toggle button text colors.
/// Active button gets white text, inactive gets gray.
/// </summary>
public class BoolToToggleTextColorConverter : IValueConverter
{
    private static readonly Color ActiveTextColor = Colors.White;
    private static readonly Color InactiveTextColor = Color.FromArgb("#888888");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isSimple = value is bool b && b;
        var mode = parameter as string ?? "simple";

        return mode == "simple"
            ? (isSimple ? ActiveTextColor : InactiveTextColor)
            : (isSimple ? InactiveTextColor : ActiveTextColor);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts content item type to accent colors.
/// </summary>
public class ContentTypeToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string type)
        {
            return type.ToLowerInvariant() switch
            {
                "text" => Color.FromArgb("#607D8B"),        // Blue-gray
                "tool_use" => Color.FromArgb("#FF9800"),    // Orange
                "tool_result" => Color.FromArgb("#795548"), // Brown
                _ => Color.FromArgb("#757575")              // Gray
            };
        }
        return Color.FromArgb("#757575");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
